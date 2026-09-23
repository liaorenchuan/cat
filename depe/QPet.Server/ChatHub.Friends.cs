using Microsoft.Data.Sqlite;
using QPet.Core;

namespace QPet.Server;

/// <summary>ChatHub 分部:好友(搜索/申请/审批/备注/删除/管理端直加)。</summary>
public partial class ChatHub
{
    // ---- 好友 ----

    /// <summary>按 UserId 取用户资料(账号/昵称 + 与我的好友/申请状态);不存在返回 null。</summary>
    public UserProfile? GetUserProfile(string me, string userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT UserId, Nickname, Account FROM users WHERE UserId = $id;";
        cmd.Parameters.AddWithValue("$id", userId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new UserProfile
        {
            UserId = reader.GetString(0),
            Nickname = reader.GetString(1),
            Account = reader.GetString(2),
            IsMe = userId == me,
            IsFriend = AreFriends(conn, me, userId),
            HasPendingRequest = HasPendingRequest(conn, me, userId),
        };
    }

    /// <summary>按账号/昵称模糊搜索用户(排除自己),带好友/申请状态。</summary>
    public List<UserSearchResult> SearchUsers(string keyword, string me)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0)
            return new List<UserSearchResult>();

        var results = new List<UserSearchResult>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT UserId, Account, Nickname FROM users
            WHERE (Account LIKE $kw OR Nickname LIKE $kw) AND UserId != $me
            ORDER BY Nickname LIMIT 20;
            """;
        cmd.Parameters.AddWithValue("$kw", $"%{keyword}%");
        cmd.Parameters.AddWithValue("$me", me);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            results.Add(new UserSearchResult
            {
                UserId = id,
                Account = reader.GetString(1),
                Nickname = reader.GetString(2),
                IsFriend = AreFriends(conn, me, id),
                HasPendingRequest = HasPendingRequest(conn, me, id),
            });
        }
        return results;
    }

    /// <summary>发好友申请(自己/重复/已是好友/已有待处理申请都会拒绝)。成功触发 FriendRequestReceived。</summary>
    public FriendRequest SendFriendRequest(string me, string toUserId, string text)
    {
        if (toUserId == me)
            throw new ArgumentException("不能添加自己");
        if (text is null || text.Length > 200)
            text = "";

        using var conn = Open();
        if (GetNickname(toUserId).Length == 0)
            throw new ArgumentException("该用户不存在");
        if (AreFriends(conn, me, toUserId))
            throw new ArgumentException("你们已经是好友");
        if (HasPendingRequest(conn, me, toUserId) || HasPendingRequest(conn, toUserId, me))
            throw new ArgumentException("已有待处理的申请");

        var req = new FriendRequest
        {
            FromUserId = me,
            FromName = GetNickname(me),
            ToUserId = toUserId,
            Text = text,
            CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO friend_requests (FromUserId, ToUserId, Text, Status, CreatedAtMs)
            VALUES ($f, $t, $x, 0, $c);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$f", req.FromUserId);
        cmd.Parameters.AddWithValue("$t", req.ToUserId);
        cmd.Parameters.AddWithValue("$x", req.Text);
        cmd.Parameters.AddWithValue("$c", req.CreatedAtMs);
        req.ReqId = (long)cmd.ExecuteScalar()!;

        FriendRequestReceived?.Invoke(req);
        return req;
    }

    /// <summary>处理申请(仅被申请人):同意 = 事务内建好友关系;拒绝只改状态。
    /// 成功触发 FriendRequestResult 与 FriendAdded(双方)。返回是否找到并处理。</summary>
    public bool RespondRequest(string me, long requestId, bool accept)
    {
        using var conn = Open();
        using var find = conn.CreateCommand();
        find.CommandText = """
            SELECT ReqId, FromUserId, ToUserId, Status, CreatedAtMs, Text FROM friend_requests
            WHERE ReqId = $r;
            """;
        find.Parameters.AddWithValue("$r", requestId);
        FriendRequest? req = null;
        using (var reader = find.ExecuteReader())
        {
            if (reader.Read() && reader.GetInt64(3) == 0 && reader.GetString(2) == me)
            {
                req = new FriendRequest
                {
                    ReqId = reader.GetInt64(0),
                    FromUserId = reader.GetString(1),
                    ToUserId = reader.GetString(2),
                    CreatedAtMs = reader.GetInt64(4),
                    Text = reader.GetString(5),
                };
            }
        }
        if (req is null)
            return false;

        req.FromName = GetNickname(req.FromUserId);

        if (accept)
        {
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE friend_requests SET Status = 1 WHERE ReqId = $r;";
                cmd.Parameters.AddWithValue("$r", requestId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR IGNORE INTO friends (UserA, UserB) VALUES ($a, $b);
                    """;
                cmd.Parameters.AddWithValue("$a", MinId(req.FromUserId, req.ToUserId));
                cmd.Parameters.AddWithValue("$b", MaxId(req.FromUserId, req.ToUserId));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        else
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE friend_requests SET Status = 2 WHERE ReqId = $r;";
            cmd.Parameters.AddWithValue("$r", requestId);
            cmd.ExecuteNonQuery();
        }

        FriendRequestResult?.Invoke(req, accept);
        FriendRequestHandled?.Invoke(req.ToUserId, requestId); // 被申请人:申请已处理
        if (accept)
            FriendAdded?.Invoke(req);
        return true;
    }

    /// <summary>
    /// 管理端直接加好友:按账号解析目标用户,直接建立双向好友关系(跳过申请/审批)。
    /// 已在好友 = false;账号不存在 / 加自己抛 ArgumentException。
    /// 成功触发 FriendAdded 事件,双方在线客户端即时刷新好友列表。
    /// </summary>
    public bool DirectAddFriend(string meUserId, string targetAccount)
    {
        targetAccount = targetAccount.Trim();
        using var conn = Open();
        using var find = conn.CreateCommand();
        find.CommandText = "SELECT UserId, Nickname FROM users WHERE Account = $a;";
        find.Parameters.AddWithValue("$a", targetAccount);
        using var reader = find.ExecuteReader();
        if (!reader.Read())
            throw new ArgumentException("该账号不存在");
        var targetUserId = reader.GetString(0);
        if (targetUserId == meUserId)
            throw new ArgumentException("不能添加自己为好友");
        if (AreFriends(conn, meUserId, targetUserId))
            return false;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO friends (UserA, UserB) VALUES ($a, $b);";
        cmd.Parameters.AddWithValue("$a", MinId(meUserId, targetUserId));
        cmd.Parameters.AddWithValue("$b", MaxId(meUserId, targetUserId));
        cmd.ExecuteNonQuery();

        // 复用申请通过事件:FromUserId = 被管用户,ToUserId = 对方。
        // PushFriendAdded 给被管用户推对方(昵称按 ID 现查),给对方推被管用户昵称(FromName)。
        FriendAdded?.Invoke(new FriendRequest
        {
            FromUserId = meUserId,
            ToUserId = targetUserId,
            FromName = GetNickname(meUserId),
        });
        return true;
    }

    /// <summary>待我处理的申请列表。</summary>
    public List<FriendRequest> GetPendingRequests(string me)
    {
        var list = new List<FriendRequest>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.ReqId, r.FromUserId, u.Nickname, r.Text, r.CreatedAtMs
            FROM friend_requests r JOIN users u ON u.UserId = r.FromUserId
            WHERE r.ToUserId = $me AND r.Status = 0
            ORDER BY r.CreatedAtMs DESC;
            """;
        cmd.Parameters.AddWithValue("$me", me);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new FriendRequest
            {
                ReqId = reader.GetInt64(0),
                FromUserId = reader.GetString(1),
                FromName = reader.GetString(2),
                Text = reader.GetString(3),
                CreatedAtMs = reader.GetInt64(4),
            });
        }
        return list;
    }

    /// <summary>我的好友列表(带备注与在线状态)。</summary>
    public List<FriendInfo> GetFriends(string me)
    {
        var list = new List<FriendInfo>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT u.UserId, u.Nickname, u.Account, f.RemarkA, f.RemarkB
            FROM friends f JOIN users u ON u.UserId = f.UserB WHERE f.UserA = $me
            UNION ALL
            SELECT u.UserId, u.Nickname, u.Account, f.RemarkA, f.RemarkB
            FROM friends f JOIN users u ON u.UserId = f.UserA WHERE f.UserB = $me
            ORDER BY Nickname;
            """;
        cmd.Parameters.AddWithValue("$me", me);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var remark = me.CompareTo(id) < 0 ? reader.GetString(3) : reader.GetString(4);
            list.Add(new FriendInfo
            {
                UserId = id,
                Account = reader.GetString(2),
                Nickname = reader.GetString(1),
                Remark = remark,
                Online = IsOnline(id),
            });
        }
        return list;
    }

    /// <summary>给对方设置备注(空 = 取消备注)。</summary>
    public bool SetFriendRemark(string me, string targetUserId, string remark)
    {
        remark = remark?.Trim() ?? "";
        if (remark.Length > 20)
            return false;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // friends 按 (小Id, 大Id) 排序存储(与 MinId/MaxId 的 CompareOrdinal 一致);
        // 备注列按「我是否排在前」决定,列名是代码内常量,非用户输入
        var remarkColumn = string.CompareOrdinal(me, targetUserId) <= 0 ? "RemarkA" : "RemarkB";
        cmd.CommandText = $"UPDATE friends SET {remarkColumn} = $r WHERE UserA = $a AND UserB = $b;";
        cmd.Parameters.AddWithValue("$a", MinId(me, targetUserId));
        cmd.Parameters.AddWithValue("$b", MaxId(me, targetUserId));
        cmd.Parameters.AddWithValue("$r", remark);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>删除好友(双方关系同时删除),成功触发 FriendDeleted 推给双方。</summary>
    public bool DeleteFriend(string me, string targetUserId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM friends WHERE UserA = $a AND UserB = $b;";
        cmd.Parameters.AddWithValue("$a", MinId(me, targetUserId));
        cmd.Parameters.AddWithValue("$b", MaxId(me, targetUserId));
        if (cmd.ExecuteNonQuery() == 0)
            return false;
        FriendDeleted?.Invoke(me, targetUserId);     // 发起方:被删的是对方
        FriendDeleted?.Invoke(targetUserId, me);     // 对方:被删的是发起方
        return true;
    }
}
