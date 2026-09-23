using System.Linq;
using Microsoft.Data.Sqlite;
using QPet.Core;

namespace QPet.Server;

/// <summary>ChatHub 分部:管理(用户列表/禁用/重置密码/删除)+ 在线状态维护。</summary>
public partial class ChatHub
{
    // ---- 管理(经 /api/admin REST 调用) ----

    /// <summary>管理:全量用户列表(含账号/注册时间/禁用/在线)。</summary>
    public List<AdminUser> GetAllUsers()
    {
        var list = new List<AdminUser>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT UserId, Account, Nickname, IsBanned, CreatedAtMs, PasswordPlain FROM users ORDER BY CreatedAtMs;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            list.Add(new AdminUser(
                id,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) > 0,
                IsOnline(id),
                reader.GetInt64(4),
                reader.GetString(5)));
        }
        return list;
    }

    /// <summary>管理:禁用/解禁账号;禁用时把在线连接踢下线。返回是否找到该用户。</summary>
    public bool SetUserBanned(string userId, bool banned)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET IsBanned = $b WHERE UserId = $u;";
        cmd.Parameters.AddWithValue("$b", banned ? 1 : 0);
        cmd.Parameters.AddWithValue("$u", userId);
        if (cmd.ExecuteNonQuery() == 0)
            return false;
        if (banned)
            RemoveUser(userId); // 踢下线(在线列表移除;已连的 WebSocket 由 REST 处理层关闭)
        return true;
    }

    /// <summary>管理:重置密码(新密码至少 4 位)。返回是否找到该用户。</summary>
    public bool ResetPassword(string userId, string newPassword)
    {
        if ((newPassword ?? "").Length < 4)
            throw new ArgumentException("密码至少 4 位");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET PasswordHash = $p, PasswordPlain = $plain WHERE UserId = $u;";
        cmd.Parameters.AddWithValue("$p", HashPassword(newPassword ?? ""));
        cmd.Parameters.AddWithValue("$plain", newPassword ?? "");
        cmd.Parameters.AddWithValue("$u", userId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 管理:彻底删除账户。好友关系/好友申请/群成员关系/该用户发的消息一起删;
    /// 其创建的群整群删除(群成员 + 群消息),不留无主空群。表间无外键,事务内逐张手动清理。
    /// </summary>
    public bool DeleteUser(string userId)
    {
        // 删除前收集:该用户的好友 / 所在群(事务提交后推 FriendDeleted / GroupUpdated,
        // 否则在线好友的列表还残留被删用户,群成员人数不刷新)
        var friendIds = new List<string>();
        var memberGroupIds = new List<string>();
        using (var probe = Open())
        {
            using (var cmd = probe.CreateCommand())
            {
                cmd.CommandText = "SELECT UserA FROM friends WHERE UserB = $u UNION SELECT UserB FROM friends WHERE UserA = $u;";
                cmd.Parameters.AddWithValue("$u", userId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    friendIds.Add(reader.GetString(0));
            }
            using (var cmd = probe.CreateCommand())
            {
                cmd.CommandText = "SELECT GroupId FROM group_members WHERE UserId = $u;";
                cmd.Parameters.AddWithValue("$u", userId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    memberGroupIds.Add(reader.GetString(0));
            }
        }

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        // 先收集该用户创建的群,连带删群数据(消息/成员/加群申请/群本体)
        var groupIds = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT GroupId FROM groups WHERE OwnerId = $u;";
            cmd.Parameters.AddWithValue("$u", userId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                groupIds.Add(reader.GetString(0));
        }
        foreach (var g in groupIds)
            DeleteGroupData(conn, tx, g);

        // 其余关联数据 + 本体;users 删 0 行 = 用户不存在,回滚全部
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM friends WHERE UserA = $u OR UserB = $u;
                DELETE FROM friend_requests WHERE FromUserId = $u OR ToUserId = $u;
                DELETE FROM group_requests WHERE FromUserId = $u;
                DELETE FROM group_members WHERE UserId = $u;
                DELETE FROM messages WHERE FromUserId = $u;
                DELETE FROM users WHERE UserId = $u;
                """;
            cmd.Parameters.AddWithValue("$u", userId);
            if (cmd.ExecuteNonQuery() == 0)
            {
                tx.Rollback();
                return false;
            }
        }
        tx.Commit();
        RemoveUser(userId); // 清在线状态(触发在线列表推送)

        // 通知其好友:好友已被删除,在线好友列表即时移除
        foreach (var fid in friendIds)
            FriendDeleted?.Invoke(fid, userId);
        // 通知其所在群:成员人数刷新(其创建的群已整群删除,GetGroup 取不到,跳过)
        foreach (var gid in memberGroupIds)
        {
            using var gconn = Open();
            var g = GetGroup(gconn, gid);
            if (g.Members.Count > 0)
                GroupUpdated?.Invoke(g);
        }
        return true;
    }

    /// <summary>注册 / 刷新在线状态与昵称(登录成功、活动帧时调用)。</summary>
    public void TouchUser(string userId, string nickname)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool changed;
        lock (_lock)
        {
            changed = !_online.TryGetValue(userId, out var old) || old.Nickname != nickname;
            _online[userId] = new UserInfo { UserId = userId, Nickname = nickname, LastSeenMs = now };
        }
        if (changed)
            UsersChanged?.Invoke();
    }

    /// <summary>把用户从在线表移除(退出登录),档案保留在数据库。</summary>
    public void RemoveUser(string userId)
    {
        bool removed;
        lock (_lock)
            removed = _online.Remove(userId);
        if (removed)
            UsersChanged?.Invoke();
    }

    /// <summary>在线用户快照(TTL 内),按最近活跃降序。</summary>
    public List<UserInfo> GetUsers()
    {
        SweepIfDue();
        lock (_lock)
            return _online.Values.OrderByDescending(u => u.LastSeenMs).ToList();
    }

    public bool IsOnline(string userId)
    {
        SweepIfDue();
        lock (_lock)
            return _online.ContainsKey(userId);
    }
}
