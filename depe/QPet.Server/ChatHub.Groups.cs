using System.Linq;
using Microsoft.Data.Sqlite;
using QPet.Core;

namespace QPet.Server;

/// <summary>ChatHub 分部:群(创建/改名/搜索/加入/退出/解散/群主管理)。</summary>
public partial class ChatHub
{
    // ---- 群 ----

    /// <summary>
    /// 创建群聊(邀请制):群里初始只有群主 1 人,选中的好友全部发邀请,
    /// 同意后加入(全部拒绝则自动取消创建)。成功触发 GroupUpdated 广播。
    /// </summary>
    public GroupInfo CreateGroup(string name, List<string> memberIds, string ownerId)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > 20)
            throw new ArgumentException("群名需为 1-20 字");

        var members = new HashSet<string> { ownerId };
        foreach (var m in memberIds ?? new())
        {
            // 容错:客户端可能传 null/空串成员 id,跳过而非崩溃
            if (!string.IsNullOrEmpty(m) && m != ownerId)
                members.Add(m);
        }
        if (members.Count < 2)
            throw new ArgumentException("至少需要邀请一个好友才能建群");

        // 防绕过:成员必须是自己好友(客户端筛选之外,服务端再校验一次)
        using var check = Open();
        foreach (var m in members)
        {
            if (m != ownerId && !AreFriends(check, ownerId, m))
                throw new ArgumentException("只能拉自己的好友进群");
        }

        var group = InsertGroup(name, ownerId, new List<string> { ownerId }); // 新群初始只有群主
        foreach (var m in members)
        {
            if (m != ownerId)
                SendGroupInvite(group.GroupId, m, ownerId); // 选中好友发邀请,同意后进群
        }
        return group;
    }

    /// <summary>
    /// 管理端创建群:群主 + 可选成员,绕过好友校验与人数限制(允许单人群)。
    /// 成员按 UserId 去重;成功触发 GroupUpdated 广播。
    /// </summary>
    public GroupInfo CreateGroupByAdmin(string name, string ownerId, List<string> memberIds)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > 20)
            throw new ArgumentException("群名需为 1-20 字");
        if (string.IsNullOrEmpty(ownerId))
            throw new ArgumentException("缺少群主");

        using var conn = Open();
        using (var chk = conn.CreateCommand())
        {
            chk.CommandText = "SELECT COUNT(*) FROM users WHERE UserId = $u;";
            chk.Parameters.AddWithValue("$u", ownerId);
            if ((long)chk.ExecuteScalar()! == 0)
                throw new ArgumentException("群主账号不存在");
        }

        var members = new HashSet<string> { ownerId };
        foreach (var m in memberIds ?? new())
        {
            // 容错:跳过 null/空串成员 id 与群主自己
            if (!string.IsNullOrEmpty(m) && m != ownerId)
                members.Add(m);
        }
        return InsertGroup(name, ownerId, members);
    }

    /// <summary>建群共用:事务内插群 + 加成员(调用方已校验名称与成员合法性),广播并返回快照。</summary>
    private GroupInfo InsertGroup(string name, string ownerId, IEnumerable<string> members)
    {
        using var conn = Open();
        var groupId = Guid.NewGuid().ToString("N");
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO groups (GroupId, Name, OwnerId, Num, CreatedAtMs)
                VALUES ($g, $n, $o, $num, $t);
                """;
            cmd.Parameters.AddWithValue("$g", groupId);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$o", ownerId);
            cmd.Parameters.AddWithValue("$num", NextGroupNum(conn));
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
        foreach (var m in members)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO group_members (GroupId, UserId) VALUES ($g, $u);";
            cmd.Parameters.AddWithValue("$g", groupId);
            cmd.Parameters.AddWithValue("$u", m);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();

        var group = GetGroup(conn, groupId);
        GroupUpdated?.Invoke(group);
        return group;
    }

    /// <summary>群改名(仅成员),成功触发 GroupUpdated 广播。</summary>
    public bool RenameGroup(string groupId, string name, string me)
    {
        name = name?.Trim() ?? "";
        if (groupId == PublicGroupId)
            return false; // 公共群全员共享,不可改名
        if (name.Length == 0 || name.Length > 20 || !IsGroupMember(groupId, me))
            return false;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE groups SET Name = $n WHERE GroupId = $g;";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.ExecuteNonQuery();

        GroupUpdated?.Invoke(GetGroup(conn, groupId));
        return true;
    }

    /// <summary>管理端改群名(管理员密码即权限,不要求是群成员)。公共群不可改名。</summary>
    public bool RenameGroupByAdmin(string groupId, string name)
    {
        name = name?.Trim() ?? "";
        if (groupId == PublicGroupId)
            return false; // 公共群全员共享,不可改名
        if (name.Length == 0 || name.Length > 20)
            return false;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE groups SET Name = $n WHERE GroupId = $g;";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$g", groupId);
        if (cmd.ExecuteNonQuery() == 0)
            return false; // 群不存在
        GroupUpdated?.Invoke(GetGroup(conn, groupId));
        return true;
    }

    /// <summary>我所在的全部群(公共群聊在列)。</summary>
    public List<GroupInfo> GetGroups(string me)
    {
        var list = new List<GroupInfo>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT g.GroupId, g.Name, g.OwnerId, g.GroupId = $pub AS IsPublic
            FROM groups g
            JOIN group_members gm ON gm.GroupId = g.GroupId
            WHERE gm.UserId = $me
            ORDER BY IsPublic DESC, g.CreatedAtMs;
            """;
        cmd.Parameters.AddWithValue("$me", me);
        cmd.Parameters.AddWithValue("$pub", PublicGroupId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(GetGroup(conn, reader.GetString(0)));
        }
        return list;
    }

    public bool IsGroupMember(string groupId, string userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM group_members WHERE GroupId = $g AND UserId = $u;";
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.Parameters.AddWithValue("$u", userId);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    /// <summary>群成员 UserId 列表(消息路由用)。</summary>
    public List<string> GetGroupMemberIds(string groupId)
    {
        using var conn = Open();
        return GetGroupMemberIds(conn, groupId);
    }

    /// <summary>群成员 UserId 列表(带连接版本,事务内/高频调用复用连接,避免重复 Open)。</summary>
    private static List<string> GetGroupMemberIds(SqliteConnection conn, string groupId)
    {
        var list = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT UserId FROM group_members WHERE GroupId = $g;";
        cmd.Parameters.AddWithValue("$g", groupId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(reader.GetString(0));
        return list;
    }

    /// <summary>是否群主。</summary>
    private static bool IsGroupOwner(SqliteConnection conn, string groupId, string userId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM groups WHERE GroupId = $g AND OwnerId = $u;";
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.Parameters.AddWithValue("$u", userId);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    /// <summary>是否群管理员(群主不算,群主用 IsGroupOwner 判定)。</summary>
    private static bool IsGroupAdmin(SqliteConnection conn, string groupId, string userId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT AdminIds FROM groups WHERE GroupId = $g;";
        cmd.Parameters.AddWithValue("$g", groupId);
        var admins = ParseAdminIds((string)cmd.ExecuteScalar()!);
        return admins.Contains(userId);
    }

    /// <summary>清除某用户的群管理员身份(移出成员时调用,避免审批推给不在群里的人)。</summary>
    private static void ClearAdminRole(SqliteConnection conn, string groupId, string userId)
    {
        using var read = conn.CreateCommand();
        read.CommandText = "SELECT AdminIds FROM groups WHERE GroupId = $g;";
        read.Parameters.AddWithValue("$g", groupId);
        var admins = ParseAdminIds((string)read.ExecuteScalar()!);
        if (!admins.Remove(userId))
            return;
        using var write = conn.CreateCommand();
        write.CommandText = "UPDATE groups SET AdminIds = $a WHERE GroupId = $g;";
        write.Parameters.AddWithValue("$a", string.Join(",", admins));
        write.Parameters.AddWithValue("$g", groupId);
        write.ExecuteNonQuery();
    }

    private GroupInfo GetGroup(SqliteConnection conn, string groupId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name, OwnerId, GroupId = $pub, Num, AdminIds FROM groups WHERE GroupId = $g;";
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.Parameters.AddWithValue("$pub", PublicGroupId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return new GroupInfo { GroupId = groupId, Name = "?", Members = new() };
        return new GroupInfo
        {
            GroupId = groupId,
            Num = reader.GetString(3),
            Name = reader.GetString(0),
            IsPublic = reader.GetInt64(2) > 0,
            OwnerId = reader.GetString(1),
            AdminIds = ParseAdminIds(reader.GetString(4)),
            Members = LoadMembers(conn, groupId),
        };
    }

    /// <summary>逗号分隔的管理员 UserId 串 → 列表(容错:空/单值)。</summary>
    private static List<string> ParseAdminIds(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>
    /// 按群名或群号模糊搜索群(全库搜,不只限我所在的群)。带当前用户的成员/群主状态,
    /// 供客户端决定按钮。群号按数字片段、群名按包含匹配,公共群排最前。
    /// </summary>
    public List<GroupSearchResult> SearchGroups(string keyword, string me)
    {
        keyword = keyword?.Trim() ?? "";
        if (keyword.Length == 0)
            return new List<GroupSearchResult>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT g.GroupId, g.Num, g.Name, g.GroupId = $pub,
                   (SELECT COUNT(*) FROM group_members gm2 WHERE gm2.GroupId = g.GroupId),
                   EXISTS(SELECT 1 FROM group_members gm3 WHERE gm3.GroupId = g.GroupId AND gm3.UserId = $me),
                   EXISTS(SELECT 1 FROM group_requests gr WHERE gr.GroupId = g.GroupId AND gr.FromUserId = $me AND gr.Status = 0)
            FROM groups g
            WHERE g.Name LIKE $kw OR g.Num LIKE $kw
            ORDER BY g.GroupId = $pub DESC, g.Name
            LIMIT 20;
            """;
        cmd.Parameters.AddWithValue("$kw", $"%{keyword}%");
        cmd.Parameters.AddWithValue("$pub", PublicGroupId);
        cmd.Parameters.AddWithValue("$me", me);
        var list = new List<GroupSearchResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var groupId = reader.GetString(0);
            list.Add(new GroupSearchResult
            {
                GroupId = groupId,
                Num = reader.GetString(1),
                Name = reader.GetString(2),
                IsPublic = reader.GetInt64(3) > 0,
                MemberCount = (int)reader.GetInt64(4),
                IsMember = reader.GetInt64(5) > 0,
                IsOwner = IsGroupOwner(conn, groupId, me),
                HasPendingRequest = reader.GetInt64(6) > 0,
            });
        }
        return list;
    }

    /// <summary>管理:全部群列表(全库,成员一次查全按群分组)。管理端加群候选用。</summary>
    public List<GroupInfo> GetAllGroups()
    {
        var list = new List<GroupInfo>();
        var index = new Dictionary<string, GroupInfo>();
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT GroupId, Num, Name, OwnerId, GroupId = $pub, AdminIds FROM groups ORDER BY GroupId = $pub DESC, Name;";
            cmd.Parameters.AddWithValue("$pub", PublicGroupId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var g = new GroupInfo
                {
                    GroupId = reader.GetString(0),
                    Num = reader.GetString(1),
                    Name = reader.GetString(2),
                    OwnerId = reader.GetString(3),
                    IsPublic = reader.GetInt64(4) > 0,
                    AdminIds = ParseAdminIds(reader.GetString(5)),
                };
                index[g.GroupId] = g;
                list.Add(g);
            }
        }
        // 成员昵称/账号(管理端群管理页展示)
        var users = new Dictionary<string, (string Nickname, string Account)>();
        using (var uc = conn.CreateCommand())
        {
            uc.CommandText = "SELECT UserId, Nickname, Account FROM users;";
            using var ur = uc.ExecuteReader();
            while (ur.Read())
                users[ur.GetString(0)] = (ur.GetString(1), ur.GetString(2));
        }
        using (var mem = conn.CreateCommand())
        {
            mem.CommandText = "SELECT GroupId, UserId FROM group_members;";
            using var mr = mem.ExecuteReader();
            while (mr.Read())
            {
                if (index.TryGetValue(mr.GetString(0), out var g))
                {
                    var member = new GroupMember { UserId = mr.GetString(1) };
                    if (users.TryGetValue(member.UserId, out var u))
                    {
                        member.Nickname = u.Nickname;
                        member.Account = u.Account;
                    }
                    g.Members.Add(member);
                }
            }
        }
        return list;
    }

    /// <summary>加入群聊(公共群所有注册用户可加;普通群须已是好友的群成员拉入)。</summary>
    public GroupInfo? JoinGroup(string groupId, string me)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT GroupId = $pub FROM groups WHERE GroupId = $g;";
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.Parameters.AddWithValue("$pub", PublicGroupId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new ArgumentException("群不存在");
        var isPublic = reader.GetInt64(0) > 0;
        if (!isPublic)
            throw new ArgumentException("该群是好友群,需由成员拉入");

        return AddMember(conn, groupId, me, $"「{GetNickname(me)}」加入了群聊");
    }

    /// <summary>
    /// 管理端直接加群:按群号(Num)查找群,直接加入(跳过申请/审批,公共群/普通群均可)。
    /// 已在该群 = false;群号不存在抛 ArgumentException。
    /// 成功发系统消息并触发 GroupUpdated 广播,在线成员即时看到成员数变化。
    /// </summary>
    public bool DirectJoinGroup(string meUserId, string groupNum)
    {
        groupNum = groupNum.Trim();
        using var conn = Open();
        using var find = conn.CreateCommand();
        find.CommandText = "SELECT GroupId FROM groups WHERE Num = $n;";
        find.Parameters.AddWithValue("$n", groupNum);
        using var reader = find.ExecuteReader();
        if (!reader.Read())
            throw new ArgumentException("该群号不存在");
        var groupId = reader.GetString(0);
        if (IsGroupMember(groupId, meUserId))
            return false;

        AddMember(conn, groupId, meUserId, $"「{GetNickname(meUserId)}」加入了群聊");
        return true;
    }

    /// <summary>加成员共用:INSERT OR IGNORE,新增时插系统消息,广播并返回群快照。</summary>
    private GroupInfo AddMember(SqliteConnection conn, string groupId, string userId, string systemText)
    {
        using var add = conn.CreateCommand();
        add.CommandText = "INSERT OR IGNORE INTO group_members (GroupId, UserId) VALUES ($g, $u);";
        add.Parameters.AddWithValue("$g", groupId);
        add.Parameters.AddWithValue("$u", userId);
        if (add.ExecuteNonQuery() > 0 && systemText.Length > 0)
            SendSystem(1, groupId, systemText);

        // 已直接入群:删除其对该群的待处理申请,并通知群主/管理员从审批列表移除
        // (防"拉进来的人还显示在群申请里";拉入 = 无需再走申请审批)
        using (var sel = conn.CreateCommand())
        {
            sel.CommandText = "SELECT ReqId FROM group_requests WHERE GroupId = $g AND FromUserId = $u AND Status = 0;";
            sel.Parameters.AddWithValue("$g", groupId);
            sel.Parameters.AddWithValue("$u", userId);
            using var r = sel.ExecuteReader();
            var ids = new List<long>();
            while (r.Read())
                ids.Add(r.GetInt64(0));
            if (ids.Count > 0)
            {
                using var del = conn.CreateCommand();
                del.CommandText = "DELETE FROM group_requests WHERE GroupId = $g AND FromUserId = $u AND Status = 0;";
                del.Parameters.AddWithValue("$g", groupId);
                del.Parameters.AddWithValue("$u", userId);
                del.ExecuteNonQuery();
                // 通知群主+管理员:该申请已消失(在线客户端审批列表即时刷新)
                using var owner = conn.CreateCommand();
                owner.CommandText = "SELECT OwnerId, AdminIds FROM groups WHERE GroupId = $g;";
                owner.Parameters.AddWithValue("$g", groupId);
                using var or = owner.ExecuteReader();
                if (or.Read())
                {
                    var approvers = ParseAdminIds(or.GetString(1)).Append(or.GetString(0)).Distinct().ToList();
                    foreach (var id in ids)
                    foreach (var ap in approvers)
                        GroupRequestHandled?.Invoke(ap, id);
                }
            }
        }

        var group = GetGroup(conn, groupId);
        GroupUpdated?.Invoke(group);
        return group;
    }

    /// <summary>退出群聊。群主退出 = 解散群(删消息/成员),群号释放。其余成员触发 GroupUpdated。</summary>
    public GroupInfo? LeaveGroup(string groupId, string me)
    {
        if (groupId == PublicGroupId)
            throw new ArgumentException("公共群不能退出");
        if (!IsGroupMember(groupId, me))
            throw new ArgumentException("你不是该群成员");
        using var conn = Open();
        if (IsGroupOwner(conn, groupId, me))
        {
            // 群主退出 = 解散:删消息 + 成员 + 群本体(原成员收 groupRemoved)
            DisbandGroup(conn, groupId, "");
            return null;
        }

        // 普通成员退出:先删成员行;退出后不足 2 人自动解散,否则插系统消息
        // (系统消息在解散场景不插:随群一起删掉没有意义)
        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM group_members WHERE GroupId = $g AND UserId = $u;";
            del.Parameters.AddWithValue("$g", groupId);
            del.Parameters.AddWithValue("$u", me);
            del.ExecuteNonQuery();
        }
        if (GetGroupMemberIds(conn, groupId).Count < 2)
        {
            DisbandGroup(conn, groupId, "群聊人数不足 2 人,已自动解散");
            return null;
        }
        SendSystem(1, groupId, $"「{GetNickname(me)}」退出了群聊");
        var updated = GetGroup(conn, groupId);
        GroupUpdated?.Invoke(updated);
        return updated;
    }

    /// <summary>
    /// 解散群(公共群防御性拒绝):事务内删消息/成员/加群申请/群邀请/群本体,
    /// 向原全体成员推 GroupRemoved(带原因),在线客户端从群列表消失;
    /// 对全部待处理邀请的被邀者推 GroupInviteHandled(非成员,不在 GroupRemoved 列表,单独通知防列表残留)。
    /// 群号不回收(历史群号永不冲突),与群主退出解散一致。
    /// </summary>
    private void DisbandGroup(SqliteConnection conn, string groupId, string reason)
    {
        if (groupId == PublicGroupId)
            return; // 公共群全员共享,任何路径都不可解散
        var members = GetGroupMemberIds(conn, groupId);
        var invitees = new List<(string UserId, long InviteId)>();
        using (var sel = conn.CreateCommand())
        {
            sel.CommandText = "SELECT ToUserId, InviteId FROM group_invites WHERE GroupId = $g AND Status = 0;";
            sel.Parameters.AddWithValue("$g", groupId);
            using var r = sel.ExecuteReader();
            while (r.Read())
                invitees.Add((r.GetString(0), r.GetInt64(1)));
        }
        using var tx = conn.BeginTransaction();
        DeleteGroupData(conn, tx, groupId);
        tx.Commit();
        foreach (var memberId in members)
            GroupRemoved?.Invoke(memberId, groupId, reason);
        foreach (var (inviteeId, inviteId) in invitees)
            GroupInviteHandled?.Invoke(inviteeId, inviteId);
    }

    /// <summary>删群的落库数据(消息/成员/加群申请/群本体)。事务由调用方管理(tx 可为 null)。</summary>
    private static void DeleteGroupData(SqliteConnection conn, SqliteTransaction? tx, string groupId)
    {
        using (var delMsg = conn.CreateCommand())
        {
            delMsg.Transaction = tx;
            delMsg.CommandText = "DELETE FROM messages WHERE ConvType = 1 AND GroupId = $g;";
            delMsg.Parameters.AddWithValue("$g", groupId);
            delMsg.ExecuteNonQuery();
        }
        using (var delMem = conn.CreateCommand())
        {
            delMem.Transaction = tx;
            delMem.CommandText = "DELETE FROM group_members WHERE GroupId = $g;";
            delMem.Parameters.AddWithValue("$g", groupId);
            delMem.ExecuteNonQuery();
        }
        using (var delReq = conn.CreateCommand())
        {
            delReq.Transaction = tx;
            delReq.CommandText = "DELETE FROM group_requests WHERE GroupId = $g;";
            delReq.Parameters.AddWithValue("$g", groupId);
            delReq.ExecuteNonQuery();
        }
        using (var delInv = conn.CreateCommand())
        {
            delInv.Transaction = tx;
            delInv.CommandText = "DELETE FROM group_invites WHERE GroupId = $g;";
            delInv.Parameters.AddWithValue("$g", groupId);
            delInv.ExecuteNonQuery();
        }
        using (var delGrp = conn.CreateCommand())
        {
            delGrp.Transaction = tx;
            delGrp.CommandText = "DELETE FROM groups WHERE GroupId = $g;";
            delGrp.Parameters.AddWithValue("$g", groupId);
            delGrp.ExecuteNonQuery();
        }
    }

    /// <summary>管理端解散群(公共群不可解散,返回 false;触发全部成员在线移除)。</summary>
    public bool DisbandGroupByAdmin(string groupId)
    {
        using var conn = Open();
        if (groupId == PublicGroupId)
            return false;
        DisbandGroup(conn, groupId, "群已被管理员解散");
        return true;
    }

    /// <summary>管理:把用户移出群聊(公共群也可移)。GroupUpdated 推给剩余成员,GroupRemoved 推给被踢者。
    /// 被踢的是群主时自动顺位转让群主:先给第一个管理员,没有管理员则给最早入群的普通成员(避免群变无主)。</summary>
    public bool KickFromGroup(string groupId, string userId)
    {
        using var conn = Open();
        var kickedIsOwner = groupId != PublicGroupId && IsGroupOwner(conn, groupId, userId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM group_members WHERE GroupId = $g AND UserId = $u;";
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.Parameters.AddWithValue("$u", userId);
        if (cmd.ExecuteNonQuery() == 0)
            return false;
        // 被移者不再是成员:其管理员身份一并清除(避免审批推送发给不在群里的人)
        ClearAdminRole(conn, groupId, userId);
        if (kickedIsOwner)
            TransferOwnership(conn, groupId); // 群主被移:群主顺位转让给剩余成员
        GroupRemoved?.Invoke(userId, groupId, ""); // 被踢者:从群列表消失(在线时即时收到)
        // 成员不足 2 人自动解散(公共群除外:注册用户都会加入,不能散):
        // 删消息/成员/申请/群本体,剩余成员(含最后一人)收解散提示
        if (groupId != PublicGroupId && GetGroupMemberIds(conn, groupId).Count < 2)
        {
            DisbandGroup(conn, groupId, "群聊人数不足 2 人,已自动解散");
            return true;
        }
        var g = GetGroup(conn, groupId);
        if (g.Members.Count > 0)
            GroupUpdated?.Invoke(g);
        return true;
    }

    /// <summary>群主被移出时的顺位转让:先给仍属群成员的第一个管理员,无管理员则给最早入群的
    /// 普通成员(group_members 默认按 rowid 即加入顺序返回);新群主不再兼管理员。
    /// 群里已无人时不动作(调用方随后按"不足 2 人"解散)。</summary>
    private static void TransferOwnership(SqliteConnection conn, string groupId)
    {
        using var read = conn.CreateCommand();
        read.CommandText = "SELECT AdminIds FROM groups WHERE GroupId = $g;";
        read.Parameters.AddWithValue("$g", groupId);
        var admins = ParseAdminIds((string)read.ExecuteScalar()!);
        var members = GetGroupMemberIds(conn, groupId);
        var newOwner = admins.FirstOrDefault(members.Contains) ?? members.FirstOrDefault();
        if (newOwner is null)
            return;
        admins.Remove(newOwner);
        using var upd = conn.CreateCommand();
        upd.CommandText = "UPDATE groups SET OwnerId = $o, AdminIds = $a WHERE GroupId = $g;";
        upd.Parameters.AddWithValue("$o", newOwner);
        upd.Parameters.AddWithValue("$a", string.Join(",", admins));
        upd.Parameters.AddWithValue("$g", groupId);
        upd.ExecuteNonQuery();
    }
}
