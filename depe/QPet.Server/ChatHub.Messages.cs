using System.Globalization;
using Microsoft.Data.Sqlite;
using QPet.Core;

namespace QPet.Server;

/// <summary>ChatHub 分部:消息(发送/入库/未读红点/已读回执/历史拉取)。</summary>
public partial class ChatHub
{
    // ---- 消息 ----

    /// <summary>
    /// 发消息。convType 0 = 私聊(convId 为对方 UserId,须是好友),1 = 群聊(convId 为群 ID,须是成员)。
    /// 校验失败抛 ArgumentException。入库触发 MessageAdded。
    /// </summary>
    public ChatMessage Send(string fromUserId, string fromName, int convType, string convId, string text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0 || text.Length > MaxTextLength)
            throw new ArgumentException($"消息需为 1-{MaxTextLength} 字");
        if (string.IsNullOrEmpty(convId))
            throw new ArgumentException("缺少会话");

        using var conn = Open();
        string? convA = null, convB = null, groupId = null;
        if (convType == 0)
        {
            if (!AreFriends(conn, fromUserId, convId))
                throw new ArgumentException("只能给好友发私聊");
            convA = MinId(fromUserId, convId);
            convB = MaxId(fromUserId, convId);
        }
        else
        {
            if (!IsGroupMember(convId, fromUserId))
                throw new ArgumentException("你不是该群成员");
            groupId = convId;
        }

        var msg = new ChatMessage
        {
            FromUserId = fromUserId,
            FromName = fromName,
            ConvType = convType,
            ConvId = convId,
            Text = text,
            Time = DateTimeOffset.Now,
            Type = 0, // 强制普通消息:系统消息只能由服务器内部 SendSystem 生成
        };
        return InsertMessage(msg);
    }

    /// <summary>入库 + 溢出裁剪 + 触发 MessageAdded(普通/系统消息共用)。</summary>
    private ChatMessage InsertMessage(ChatMessage msg)
    {
        string? convA = null, convB = null, groupId = null;
        if (msg.ConvType == 0)
        {
            convA = MinId(msg.FromUserId, msg.ConvId);
            convB = MaxId(msg.FromUserId, msg.ConvId);
        }
        else
        {
            groupId = msg.ConvId;
        }

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (FromUserId, FromName, ConvType, ConvA, ConvB, GroupId, Text, Time, Type)
            VALUES ($f, $n, $c, $a, $b, $g, $x, $t, $y);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$f", msg.FromUserId);
        cmd.Parameters.AddWithValue("$n", msg.FromName);
        cmd.Parameters.AddWithValue("$c", msg.ConvType);
        cmd.Parameters.AddWithValue("$a", (object?)convA ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$b", (object?)convB ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$g", (object?)groupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$x", msg.Text);
        cmd.Parameters.AddWithValue("$t", msg.Time.ToString("O"));
        cmd.Parameters.AddWithValue("$y", msg.Type);
        msg.Seq = (long)cmd.ExecuteScalar()!;

        TrimIfOverflow();
        if (msg.FromUserId.Length > 0)
            TouchUser(msg.FromUserId, msg.FromName);
        // 未读红点:接收者各 +1(发送者不加;系统消息 FromUserId 空,群内所有人 +1)。
        // 接收者视角会话键:私聊 "0:发送者",群聊 "1:群"。
        var recipients = GetMessageRecipients(msg);
        if (msg.FromUserId.Length > 0)
            recipients.Remove(msg.FromUserId);
        if (recipients.Count > 0)
            IncrementUnread(recipients, ReceiverConvKey(msg.ConvType, msg.ConvId, msg.FromUserId));
        MessageAdded?.Invoke(msg);
        return msg;
    }

    /// <summary>
    /// 内部系统消息:成员变动提示。FromUserId/FromName 为空,Type=1,ConvType=1 走群路由
    /// (GetMessageRecipients 按群成员推),不经过 Send() 的发言校验。
    /// </summary>
    private ChatMessage SendSystem(int convType, string convId, string text) =>
        InsertMessage(new ChatMessage
        {
            ConvType = convType,
            ConvId = convId,
            Text = text,
            Time = DateTimeOffset.Now,
            Type = 1,
        });

    /// <summary>一条消息的接收者 UserId 列表(路由推送用)。</summary>
    public List<string> GetMessageRecipients(ChatMessage msg)
    {
        if (msg.ConvType == 1)
            return GetGroupMemberIds(msg.ConvId);
        return new List<string> { MinId(msg.FromUserId, msg.ConvId), MaxId(msg.FromUserId, msg.ConvId) };
    }

    // ---- 未读红点(服务器权威计数:别人消息/系统消息 +1,打开会话归零) ----

    /// <summary>给一批用户某会话未读 +1(新消息送达时,发送者不加)。</summary>
    public Dictionary<string, int> IncrementUnread(IEnumerable<string> userIds, string convKey)
    {
        var after = new Dictionary<string, int>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO unread_counts (UserId, ConvKey, Count) VALUES ($u, $k, 1)
            ON CONFLICT(UserId, ConvKey) DO UPDATE SET Count = Count + 1
            RETURNING Count;
            """;
        cmd.Parameters.AddWithValue("$k", convKey);
        foreach (var u in userIds)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$u", u);
            cmd.Parameters.AddWithValue("$k", convKey);
            var r = cmd.ExecuteScalar();
            after[u] = r is null ? 1 : Convert.ToInt32(r);
        }
        return after;
    }

    /// <summary>打开会话:该用户该会话未读归零(会话键是用户视角:私聊 "0:对方",群聊 "1:群")。</summary>
    public void ClearUnread(string userId, string convKey)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM unread_counts WHERE UserId = $u AND ConvKey = $k;";
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$k", convKey);
        cmd.ExecuteNonQuery();
    }

    /// <summary>某用户某会话当前未读数(推送消息帧时附带)。</summary>
    public int GetUnreadCount(string userId, string convKey)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Count FROM unread_counts WHERE UserId = $u AND ConvKey = $k;";
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$k", convKey);
        var r = cmd.ExecuteScalar();
        return r is null ? 0 : Convert.ToInt32(r);
    }

    /// <summary>某用户全部未读数(welcome 快照下发,key = 用户视角会话键)。</summary>
    public Dictionary<string, int> GetUnreadCounts(string userId)
    {
        var map = new Dictionary<string, int>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ConvKey, Count FROM unread_counts WHERE UserId = $u;";
        cmd.Parameters.AddWithValue("$u", userId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }

    // ---- 已读回执 ----

    /// <summary>会话键:私聊 "0:a:b"(两 Id 排序),群聊 "1:g"(与消息路由一致)。</summary>
    private static string ConvKey(int convType, string a, string b) =>
        convType == 0 ? $"{Protocol.ConvKeyPrivate}{MinId(a, b)}:{MaxId(a, b)}" : $"{Protocol.ConvKeyGroup}{b}";

    /// <summary>
    /// 上报已读:该用户在某会话的游标推进到 seq(只进不退)。校验会话可见性(私聊须是好友/群须是成员)。
    /// 返回是否真的推进(没推进 = 重复上报,无需推送回执)。
    /// </summary>
    public bool MarkRead(string userId, int convType, string convId, long seq)
    {
        if (convType == 0)
        {
            if (!IsFriend(userId, convId))
                return false;
        }
        else
        {
            if (!IsGroupMember(convId, userId))
                return false;
        }
        if (seq <= 0)
            return false;

        var key = ConvKey(convType, userId, convId);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conv_reads (UserId, ConvKey, LastReadSeq) VALUES ($u, $k, $s)
            ON CONFLICT (UserId, ConvKey) DO UPDATE SET LastReadSeq = MAX(LastReadSeq, $s);
            SELECT LastReadSeq FROM conv_reads WHERE UserId = $u AND ConvKey = $k;
            """;
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$s", seq);
        return (long)cmd.ExecuteScalar()! == seq;
    }

    /// <summary>
    /// 当前用户参与的会话中,其他人的已读游标快照(welcome 下发,客户端重连/重启后已读标记不丢)。
    /// ConvId 转为「对 me 视角」:私聊 = 对方 UserId,群聊 = 群 ID。
    /// </summary>
    public List<ReadState> GetReadStates(string me)
    {
        var list = new List<ReadState>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT UserId, ConvKey, LastReadSeq FROM conv_reads
            WHERE substr(ConvKey, 1, 2) = '{Protocol.ConvKeyPrivate}'
               OR (substr(ConvKey, 1, 2) = '{Protocol.ConvKeyGroup}' AND EXISTS (
                    SELECT 1 FROM group_members gm
                    WHERE gm.GroupId = substr(ConvKey, 3) AND gm.UserId = $me));
            """;
        cmd.Parameters.AddWithValue("$me", me);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var userId = reader.GetString(0);
            var key = reader.GetString(1);
            var seq = reader.GetInt64(2);
            if (userId == me || seq <= 0)
                continue;
            // 私聊键 "0:a:b":对方 = 含 me 之外那个;群聊键 "1:g"
            if (key.StartsWith(Protocol.ConvKeyPrivate, StringComparison.Ordinal))
            {
                var parts = key.Split(':');
                if (parts.Length != 3)
                    continue;
                var other = parts[1] == me ? parts[2] : parts[1];
                list.Add(new ReadState { ConvType = 0, ConvId = other, ReaderUserId = userId, Seq = seq });
            }
            else
            {
                list.Add(new ReadState { ConvType = 1, ConvId = key.Substring(Protocol.ConvKeyGroup.Length), ReaderUserId = userId, Seq = seq });
            }
        }
        return list;
    }

    /// <summary>
    /// 增量拉取:Seq > since 且可见(私聊含我 / 群聊我是成员),升序。
    /// </summary>
    public List<ChatMessage> GetMessages(long since, string me)
    {
        SweepIfDue();
        var list = new List<ChatMessage>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.Seq, m.FromUserId, m.FromName, m.ConvType,
                   COALESCE(m.ConvA, ''), COALESCE(m.ConvB, ''),
                   COALESCE(m.GroupId, ''), m.Text, m.Time, m.Type
            FROM messages m
            LEFT JOIN group_members gm ON m.ConvType = 1 AND gm.GroupId = m.GroupId AND gm.UserId = $me
            WHERE m.Seq > $since
              AND (m.ConvType = 0 AND (m.ConvA = $me OR m.ConvB = $me)
                   OR m.ConvType = 1 AND gm.UserId IS NOT NULL)
            ORDER BY m.Seq;
            """;
        cmd.Parameters.AddWithValue("$since", since);
        cmd.Parameters.AddWithValue("$me", me);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var convA = reader.GetString(4);
            var convB = reader.GetString(5);
            list.Add(new ChatMessage
            {
                Seq = reader.GetInt64(0),
                FromUserId = reader.GetString(1),
                FromName = reader.GetString(2),
                ConvType = (int)reader.GetInt64(3),
                ConvId = reader.GetInt64(3) == 0 ? (convA == me ? convB : convA) : reader.GetString(6),
                Text = reader.GetString(7),
                Time = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                Type = (int)reader.GetInt64(9),
            });
        }
        return list;
    }

    /// <summary>当前最大消息序号(重连 welcome 帧用)。</summary>
    public long GetLastSeq()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(Seq), 0) FROM messages;";
        return (long)cmd.ExecuteScalar()!;
    }
}
