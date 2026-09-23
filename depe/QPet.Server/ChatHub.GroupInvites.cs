using Microsoft.Data.Sqlite;
using QPet.Core;

namespace QPet.Server;

/// <summary>ChatHub 分部:群邀请(拉人进群需被邀者同意,镜像好友申请闭环)。</summary>
public partial class ChatHub
{
    // ---- 群邀请(被拉进群需同意) ----

    /// <summary>
    /// 发群邀请(群成员拉自己的好友):校验(我是成员/对方是我的好友/对方不在群里/无重复待处理邀请)
    /// → 插入 group_invites 并推给被邀者(GroupInviteReceived)。同意后才加成员。
    /// </summary>
    public GroupInvite SendGroupInvite(string groupId, string toUserId, string me)
    {
        if (!IsGroupMember(groupId, me))
            throw new ArgumentException("你不是该群成员");
        if (string.IsNullOrEmpty(toUserId))
            throw new ArgumentException("缺少被邀请的用户");
        using var conn = Open();
        if (!AreFriends(conn, me, toUserId))
            throw new ArgumentException("只能邀请自己的好友进群");
        if (IsGroupMember(groupId, toUserId))
            throw new ArgumentException("对方已在该群");
        using (var dup = conn.CreateCommand())
        {
            dup.CommandText = "SELECT COUNT(*) FROM group_invites WHERE GroupId = $g AND ToUserId = $u AND Status = 0;";
            dup.Parameters.AddWithValue("$g", groupId);
            dup.Parameters.AddWithValue("$u", toUserId);
            if ((long)dup.ExecuteScalar()! > 0)
                throw new ArgumentException("已邀请,等待对方同意");
        }

        var invite = new GroupInvite
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
                INSERT INTO group_invites (GroupId, FromUserId, ToUserId, Status, CreatedAtMs)
                VALUES ($g, $f, $t, 0, $c);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$g", groupId);
            insert.Parameters.AddWithValue("$f", me);
            insert.Parameters.AddWithValue("$t", toUserId);
            insert.Parameters.AddWithValue("$c", invite.CreatedAtMs);
            invite.InviteId = (long)insert.ExecuteScalar()!;
        }

        GroupInviteReceived?.Invoke(toUserId, invite);
        return invite;
    }

    /// <summary>
    /// 被邀者审批群邀请(仅邀请对象本人)。同意 = 事务内加成员 + 系统消息并广播 GroupUpdated;
    /// 拒绝只改状态。成功后触发 GroupInviteResult(发起者)与 GroupInviteHandled(被邀者清红点)。
    /// 拒绝后若群里仅群主 1 人且无其他待处理邀请 → 自动取消创建(解散群)。
    /// </summary>
    public void RespondGroupInvite(string me, long inviteId, bool accept)
    {
        using var conn = Open();
        GroupInvite? invite = null;
        using (var find = conn.CreateCommand())
        {
            find.CommandText = """
                SELECT i.InviteId, i.GroupId, g.Name, i.FromUserId, u.Nickname, i.CreatedAtMs, i.Status, i.ToUserId
                FROM group_invites i
                JOIN groups g ON g.GroupId = i.GroupId
                JOIN users u ON u.UserId = i.FromUserId
                WHERE i.InviteId = $r;
                """;
            find.Parameters.AddWithValue("$r", inviteId);
            using var reader = find.ExecuteReader();
            if (reader.Read() && reader.GetInt64(6) == 0 && reader.GetString(7) == me)
            {
                invite = new GroupInvite
                {
                    InviteId = reader.GetInt64(0),
                    GroupId = reader.GetString(1),
                    GroupName = reader.GetString(2),
                    FromUserId = reader.GetString(3),
                    FromName = reader.GetString(4),
                    CreatedAtMs = reader.GetInt64(5),
                };
            }
        }
        if (invite is null)
            throw new ArgumentException("邀请不存在或已处理");

        if (accept)
        {
            using var tx = conn.BeginTransaction();
            using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = "UPDATE group_invites SET Status = 1 WHERE InviteId = $r;";
                upd.Parameters.AddWithValue("$r", inviteId);
                upd.ExecuteNonQuery();
            }
            using (var mem = conn.CreateCommand())
            {
                mem.Transaction = tx;
                mem.CommandText = "INSERT OR IGNORE INTO group_members (GroupId, UserId) VALUES ($g, $u);";
                mem.Parameters.AddWithValue("$g", invite.GroupId);
                mem.Parameters.AddWithValue("$u", me);
                mem.ExecuteNonQuery();
            }
            tx.Commit();
            SendSystem(1, invite.GroupId, $"「{invite.FromName}」邀请「{GetNickname(me)}」加入群聊");
            var group = GetGroup(conn, invite.GroupId);
            if (group.Members.Count > 0)
                GroupUpdated?.Invoke(group);
        }
        else
        {
            using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE group_invites SET Status = 2 WHERE InviteId = $r;";
            upd.Parameters.AddWithValue("$r", inviteId);
            upd.ExecuteNonQuery();
            // 创建群的邀请被拒光:群里只剩群主且无其他待处理邀请 → 自动取消创建(解散)
            var pending = 0;
            using (var cnt = conn.CreateCommand())
            {
                cnt.CommandText = "SELECT COUNT(*) FROM group_invites WHERE GroupId = $g AND Status = 0;";
                cnt.Parameters.AddWithValue("$g", invite.GroupId);
                pending = (int)(long)cnt.ExecuteScalar()!;
            }
            if (GetGroupMemberIds(conn, invite.GroupId).Count <= 1 && pending == 0)
                DisbandGroup(conn, invite.GroupId, "所有邀请被拒绝,群创建已取消");
        }

        GroupInviteResult?.Invoke(invite, accept);
        GroupInviteHandled?.Invoke(me, inviteId);
    }

    /// <summary>我收到的待处理群邀请(welcome 快照用,带群名与发起者昵称)。</summary>
    public List<GroupInvite> GetPendingGroupInvites(string me)
    {
        var list = new List<GroupInvite>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.InviteId, i.GroupId, g.Name, i.FromUserId, u.Nickname, i.CreatedAtMs
            FROM group_invites i
            JOIN groups g ON g.GroupId = i.GroupId
            JOIN users u ON u.UserId = i.FromUserId
            WHERE i.ToUserId = $me AND i.Status = 0
            ORDER BY i.CreatedAtMs DESC;
            """;
        cmd.Parameters.AddWithValue("$me", me);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new GroupInvite
            {
                InviteId = reader.GetInt64(0),
                GroupId = reader.GetString(1),
                GroupName = reader.GetString(2),
                FromUserId = reader.GetString(3),
                FromName = reader.GetString(4),
                CreatedAtMs = reader.GetInt64(5),
            });
        }
        return list;
    }
}
