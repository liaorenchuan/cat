using System.Linq;
using Microsoft.Data.Sqlite;
using QPet.Core;

namespace QPet.Server;

/// <summary>ChatHub 分部:群申请(发送/审批/管理员设置/成员移出)。</summary>
public partial class ChatHub
{
    // ---- 群申请(群主/管理员审批) ----

    /// <summary>
    /// 发加群申请(须群存在且我不是成员;同一群已有待处理申请会拒绝)。
    /// 成功插入 group_requests 并推给群主 + 所有管理员(GroupRequestReceived)。
    /// </summary>
    public GroupRequest SendGroupRequest(string groupId, string me)
    {
        using var conn = Open();
        string ownerId;
        var adminIds = new List<string>();
        using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT Name, OwnerId, AdminIds FROM groups WHERE GroupId = $g;";
            find.Parameters.AddWithValue("$g", groupId);
            using var reader = find.ExecuteReader();
            if (!reader.Read())
                throw new ArgumentException("群不存在");
            ownerId = reader.GetString(1);
            adminIds = ParseAdminIds(reader.GetString(2));
        }
        if (IsGroupMember(groupId, me))
            throw new ArgumentException("你已是该群成员");
        using (var dup = conn.CreateCommand())
        {
            dup.CommandText = "SELECT COUNT(*) FROM group_requests WHERE GroupId = $g AND FromUserId = $u AND Status = 0;";
            dup.Parameters.AddWithValue("$g", groupId);
            dup.Parameters.AddWithValue("$u", me);
            if ((long)dup.ExecuteScalar()! > 0)
                throw new ArgumentException("已有待处理的申请");
        }

        var req = new GroupRequest
        {
            GroupId = groupId,
            GroupName = GetGroupName(conn, groupId),
            FromUserId = me,
            FromName = GetNickname(me),
            CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO group_requests (GroupId, FromUserId, Status, CreatedAtMs)
                VALUES ($g, $u, 0, $c);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$g", groupId);
            insert.Parameters.AddWithValue("$u", me);
            insert.Parameters.AddWithValue("$c", req.CreatedAtMs);
            req.ReqId = (long)insert.ExecuteScalar()!;
        }

        // 推给群主与所有管理员(去重)
        var targets = adminIds.Append(ownerId).Distinct().ToList();
        foreach (var target in targets)
            GroupRequestReceived?.Invoke(target, req);
        return req;
    }

    /// <summary>
    /// 审批加群申请(仅群主/管理员)。同意 = 事务内加成员并广播 GroupUpdated;
    /// 拒绝只改状态。成功触发 GroupRequestResult(申请人)与 GroupRequestHandled(审批者们)。
    /// 返回是否找到并处理。
    /// </summary>
    public bool RespondGroupRequest(string me, long requestId, bool accept)
    {
        using var conn = Open();
        string ownerId;
        List<string> approverIds;
        GroupRequest? req = null;
        using (var find = conn.CreateCommand())
        {
            find.CommandText = """
                SELECT r.ReqId, r.GroupId, r.FromUserId, r.Status, r.CreatedAtMs, g.Name, g.OwnerId, g.AdminIds
                FROM group_requests r JOIN groups g ON g.GroupId = r.GroupId
                WHERE r.ReqId = $r;
                """;
            find.Parameters.AddWithValue("$r", requestId);
            using var reader = find.ExecuteReader();
            if (reader.Read() && reader.GetInt64(3) == 0)
            {
                req = new GroupRequest
                {
                    ReqId = reader.GetInt64(0),
                    GroupId = reader.GetString(1),
                    FromUserId = reader.GetString(2),
                    CreatedAtMs = reader.GetInt64(4),
                    GroupName = reader.GetString(5),
                };
            }
        }
        if (req is null)
            return false;

        using (var owner = conn.CreateCommand())
        {
            owner.CommandText = "SELECT OwnerId, AdminIds FROM groups WHERE GroupId = $g;";
            owner.Parameters.AddWithValue("$g", req.GroupId);
            using var reader = owner.ExecuteReader();
            if (!reader.Read())
                return false;
            ownerId = reader.GetString(0);
            approverIds = ParseAdminIds(reader.GetString(1)).Append(ownerId).Distinct().ToList();
        }
        if (ownerId != me && !approverIds.Contains(me))
            throw new ArgumentException("只有群主或管理员能审批");

        if (accept)
        {
            using var tx = conn.BeginTransaction();
            using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = "UPDATE group_requests SET Status = 1 WHERE ReqId = $r;";
                upd.Parameters.AddWithValue("$r", requestId);
                upd.ExecuteNonQuery();
            }
            using (var mem = conn.CreateCommand())
            {
                mem.Transaction = tx;
                mem.CommandText = "INSERT OR IGNORE INTO group_members (GroupId, UserId) VALUES ($g, $u);";
                mem.Parameters.AddWithValue("$g", req.GroupId);
                mem.Parameters.AddWithValue("$u", req.FromUserId);
                mem.ExecuteNonQuery();
            }
            tx.Commit();
            GroupUpdated?.Invoke(GetGroup(conn, req.GroupId));
        }
        else
        {
            using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE group_requests SET Status = 2 WHERE ReqId = $r;";
            upd.Parameters.AddWithValue("$r", requestId);
            upd.ExecuteNonQuery();
        }

        GroupRequestResult?.Invoke(req, accept);
        foreach (var approver in approverIds)
            GroupRequestHandled?.Invoke(approver, requestId);
        return true;
    }

    /// <summary>设置/取消群管理员(仅群主)。更新 AdminIds 并广播 GroupUpdated。</summary>
    public GroupInfo? SetGroupAdmin(string groupId, string targetUserId, bool isAdmin, string me)
    {
        using var conn = Open();
        if (!IsGroupOwner(conn, groupId, me))
            throw new ArgumentException("只有群主能设置管理员");
        if (!IsGroupMember(groupId, targetUserId))
            throw new ArgumentException("对方不是群成员");

        string adminIds;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT AdminIds FROM groups WHERE GroupId = $g;";
            read.Parameters.AddWithValue("$g", groupId);
            adminIds = (string)read.ExecuteScalar()!;
        }
        var admins = ParseAdminIds(adminIds);
        if (isAdmin && !admins.Contains(targetUserId))
            admins.Add(targetUserId);
        if (!isAdmin)
            admins.Remove(targetUserId);

        using var write = conn.CreateCommand();
        write.CommandText = "UPDATE groups SET AdminIds = $a WHERE GroupId = $g;";
        write.Parameters.AddWithValue("$a", string.Join(",", admins));
        write.Parameters.AddWithValue("$g", groupId);
        write.ExecuteNonQuery();

        SendSystem(1, groupId, isAdmin
            ? $"「{GetNickname(me)}」将「{GetNickname(targetUserId)}」设为管理员"
            : $"「{GetNickname(me)}」取消了「{GetNickname(targetUserId)}」的管理员");
        var group = GetGroup(conn, groupId);
        GroupUpdated?.Invoke(group);
        return group;
    }

    /// <summary>
    /// 群主或管理员把成员移出群聊:校验 + 删成员行 + 清被移者管理员身份 + 系统消息
    /// + 被移者收 GroupRemoved(带原因) + 剩余成员收 GroupUpdated。
    /// 群主可移任何人(除自己/群主);管理员可移普通成员(除自己/群主/其他管理员)。
    /// 公共群 OwnerId 为空,群主判定天然拒绝,仅管理员可移。
    /// </summary>
    public GroupInfo? RemoveGroupMember(string groupId, string targetUserId, string me)
    {
        if (!IsGroupMember(groupId, me))
            throw new ArgumentException("你不是该群成员");
        using var conn = Open();
        var isOwner = IsGroupOwner(conn, groupId, me);
        if (!isOwner && !IsGroupAdmin(conn, groupId, me))
            throw new ArgumentException("只有群主或管理员能移出成员");
        if (targetUserId == me)
            throw new ArgumentException("不能移出自己");
        if (IsGroupOwner(conn, groupId, targetUserId))
            throw new ArgumentException("不能移出群主");
        // 管理员不能移出其他管理员(群主可移出管理员)
        if (!isOwner && IsGroupAdmin(conn, groupId, targetUserId))
            throw new ArgumentException("管理员不能移出其他管理员");
        if (!IsGroupMember(groupId, targetUserId))
            throw new ArgumentException("对方不是群成员");

        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM group_members WHERE GroupId = $g AND UserId = $u;";
            del.Parameters.AddWithValue("$g", groupId);
            del.Parameters.AddWithValue("$u", targetUserId);
            del.ExecuteNonQuery();
        }
        ClearAdminRole(conn, groupId, targetUserId); // 群主移出管理员:其管理员身份一并清除
        // 先删成员行:被移者不在接收者列表,收 groupRemoved 提示;
        // 剩余不足 2 人自动解散(只剩群主),否则插系统消息
        GroupRemoved?.Invoke(targetUserId, groupId, $"你已被移出群聊「{GetGroupName(conn, groupId)}」");
        if (GetGroupMemberIds(conn, groupId).Count < 2)
        {
            DisbandGroup(conn, groupId, "群聊人数不足 2 人,已自动解散");
            return null;
        }
        SendSystem(1, groupId, $"「{GetNickname(me)}」将「{GetNickname(targetUserId)}」移出群聊");
        var group = GetGroup(conn, groupId);
        if (group.Members.Count > 0)
            GroupUpdated?.Invoke(group);
        return group;
    }

    /// <summary>待我审批的加群申请(我是群主或管理员,Status=0)。welcome 快照用。</summary>
    public List<GroupRequest> GetGroupRequests(string me)
    {
        var list = new List<GroupRequest>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.ReqId, r.GroupId, g.Name, r.FromUserId, u.Nickname, r.CreatedAtMs
            FROM group_requests r
            JOIN groups g ON g.GroupId = r.GroupId
            JOIN users u ON u.UserId = r.FromUserId
            WHERE r.Status = 0 AND (g.OwnerId = $me OR (',' || g.AdminIds || ',') LIKE '%,' || $me || ',%')
            ORDER BY r.CreatedAtMs DESC;
            """;
        cmd.Parameters.AddWithValue("$me", me);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new GroupRequest
            {
                ReqId = reader.GetInt64(0),
                GroupId = reader.GetString(1),
                GroupName = reader.GetString(2),
                FromUserId = reader.GetString(3),
                FromName = reader.GetString(4),
                CreatedAtMs = reader.GetInt64(5),
            });
        }
        return list;
    }

    private static string GetGroupName(SqliteConnection conn, string groupId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name FROM groups WHERE GroupId = $g;";
        cmd.Parameters.AddWithValue("$g", groupId);
        return cmd.ExecuteScalar() as string ?? "?";
    }

    /// <summary>群成员列表(昵称 + 当前在线状态)。</summary>
    private List<GroupMember> LoadMembers(SqliteConnection conn, string groupId)
    {
        var members = new List<GroupMember>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT u.UserId, u.Nickname FROM group_members gm
            JOIN users u ON u.UserId = gm.UserId
            WHERE gm.GroupId = $g ORDER BY u.Nickname;
            """;
        cmd.Parameters.AddWithValue("$g", groupId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var userId = reader.GetString(0);
            members.Add(new GroupMember { UserId = userId, Nickname = reader.GetString(1), Online = IsOnline(userId) });
        }
        return members;
    }
}
