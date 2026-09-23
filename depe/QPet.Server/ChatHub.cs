using Microsoft.Data.Sqlite;
using QPet.Core;

namespace QPet.Server;

/// <summary>
/// 局域网 IM 中枢(服务器端):账号/好友/群聊/消息全部持久化到 SQLite,在线状态维护在内存。
/// 线程安全:所有公有方法在 lock 内操作,返回快照;事件由调用线程触发(订阅方自行回 UI 线程)。
/// 所有客户端(电脑/手机)经 SyncServer WebSocket 端点访问同一实例。
/// </summary>
public partial class ChatHub
{
    /// <summary>在线超时(毫秒),超过判定离线(10 倍心跳间隔,抗网络抖动)。</summary>
    public const long UserTtlMs = 30_000;

    /// <summary>内存消息上限,超出删最旧。</summary>
    public const int MaxMessages = 2000;

    /// <summary>单条消息最长字符数。</summary>
    public const int MaxTextLength = 500;

    /// <summary>公共群聊的固定群 ID(所有注册用户自动加入)。</summary>
    public const string PublicGroupId = "public";

    /// <summary>
    /// 接收者视角的未读会话键:私聊 "0:发送者"(= 接收者眼中的对方),群聊 "1:群 ID"。
    /// 三处共用统一语义:InsertMessage 计未读、Read 归零、RouteMessage 推红点。
    /// </summary>
    public static string ReceiverConvKey(int convType, string convId, string fromUserId) =>
        convType == 1 ? Protocol.ConvKeyGroup + convId : Protocol.ConvKeyPrivate + fromUserId;

    private readonly object _lock = new();
    private readonly string _dbPath;
    private readonly Dictionary<string, UserInfo> _online = new(); // UserId → 在线用户(带 LastSeen)
    private DateTime _lastSweep = DateTime.UtcNow;

    /// <summary>新消息落地(服务器已赋值 Seq/Time)。</summary>
    public event Action<ChatMessage>? MessageAdded;

    /// <summary>在线用户变化(登录/掉线/改昵称)。</summary>
    public event Action? UsersChanged;

    /// <summary>收到新好友申请,推给被申请人。</summary>
    public event Action<FriendRequest>? FriendRequestReceived;

    /// <summary>申请被处理(同意/拒绝),推给被申请人:参数 = (接收方 UserId, 申请 ID)。</summary>
    public event Action<string, long>? FriendRequestHandled; // toUserId, requestId

    /// <summary>申请有结果(同意/拒绝),推给申请人。</summary>
    public event Action<FriendRequest, bool>? FriendRequestResult;

    /// <summary>成为好友(申请同意),推给双方刷新列表。</summary>
    public event Action<FriendRequest>? FriendAdded;

    /// <summary>删除好友(任一方发起),推给双方:参数 = (接收方 UserId, 被删好友 UserId)。</summary>
    public event Action<string, string>? FriendDeleted; // toUserId, deletedFriendId

    /// <summary>群信息变化(建群/改名/成员变动),推给全体成员。</summary>
    public event Action<GroupInfo>? GroupUpdated;

    /// <summary>群被解散(群主退出),推给原全体成员:参数 = (成员 UserId, 群 ID)。</summary>
    public event Action<string, string, string>? GroupRemoved; // memberUserId, groupId, reason(移出提示;解散为空)

    /// <summary>收到加群申请,推给群主/管理员:参数 = (接收方 UserId, 申请)。</summary>
    public event Action<string, GroupRequest>? GroupRequestReceived; // toUserId, request

    /// <summary>加群申请有结果(同意/拒绝),推给申请人。</summary>
    public event Action<GroupRequest, bool>? GroupRequestResult; // request, accepted

    /// <summary>加群申请被处理,推给群主/管理员刷新列表:参数 = (接收方 UserId, 申请 ID)。</summary>
    public event Action<string, long>? GroupRequestHandled; // toUserId, requestId

    /// <summary>收到群邀请(被拉进群),推给被邀者:参数 = (接收方 UserId, 邀请)。</summary>
    public event Action<string, GroupInvite>? GroupInviteReceived; // toUserId, invite

    /// <summary>群邀请有结果(同意/拒绝),推给发起者。</summary>
    public event Action<GroupInvite, bool>? GroupInviteResult; // invite, accepted

    /// <summary>群邀请被处理,推给被邀者刷新列表:参数 = (接收方 UserId, 邀请 ID)。</summary>
    public event Action<string, long>? GroupInviteHandled; // toUserId, inviteId

    /// <summary>用户昵称变更,推给其好友。</summary>
    public event Action<string, string>? ProfileUpdated; // userId, newNickname

    public ChatHub(string dbPath)
    {
        _dbPath = dbPath;
        EnsureSchema();
    }

    /// <summary>
    /// 建表 + 迁移。schema 版本记录在 PRAGMA user_version:
    ///   v0 → v1:建全表(含历史补列)、确保公共群、老群补号;全部幂等,失败重跑安全。
    /// 已是最新版本直接跳过——历史一次性修复不再每次启动重复执行。
    /// </summary>
    private void EnsureSchema()
    {
        using var conn = Open();

        // 史前库防御:users 表存在但无 Account 列(早期测试库形态),SQL 增量迁移救不回,
        // 显式报错退出,不再整体 DROP 重建丢数据。
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='users';";
            if ((long)probe.ExecuteScalar()! > 0)
            {
                probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('users') WHERE name='Account';";
                if ((long)probe.ExecuteScalar()! == 0)
                    throw new InvalidOperationException(
                        $"数据库过旧(users 表无 Account 列),无法自动迁移,请用新库重建:{_dbPath}");
            }
        }

        using (var ver = conn.CreateCommand())
        {
            ver.CommandText = "PRAGMA user_version;";
            if ((long)ver.ExecuteScalar()! >= SchemaVersion)
                return; // 已是最新,跳过迁移
        }

        // 建全表。列定义即当前完整形态:新库一次建全,老库由下方 EnsureColumn 幂等补齐
        using var create = conn.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                UserId TEXT PRIMARY KEY,
                Account TEXT NOT NULL UNIQUE,
                PasswordHash TEXT NOT NULL,
                PasswordPlain TEXT NOT NULL DEFAULT '',
                Nickname TEXT NOT NULL,
                PetSize REAL NOT NULL DEFAULT 200,
                CreatedAtMs INTEGER NOT NULL,
                IsBanned INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS friends (
                UserA TEXT NOT NULL,
                UserB TEXT NOT NULL,
                RemarkA TEXT NOT NULL DEFAULT '',
                RemarkB TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (UserA, UserB));
            CREATE TABLE IF NOT EXISTS friend_requests (
                ReqId INTEGER PRIMARY KEY AUTOINCREMENT,
                FromUserId TEXT NOT NULL,
                ToUserId TEXT NOT NULL,
                Text TEXT NOT NULL,
                Status INTEGER NOT NULL DEFAULT 0,
                CreatedAtMs INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS groups (
                GroupId TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                OwnerId TEXT NOT NULL DEFAULT '',
                Num TEXT NOT NULL DEFAULT '',
                AdminIds TEXT NOT NULL DEFAULT '',
                CreatedAtMs INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS group_members (
                GroupId TEXT NOT NULL,
                UserId TEXT NOT NULL,
                PRIMARY KEY (GroupId, UserId));
            CREATE TABLE IF NOT EXISTS group_requests (
                ReqId INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId TEXT NOT NULL,
                FromUserId TEXT NOT NULL,
                Status INTEGER NOT NULL DEFAULT 0,
                CreatedAtMs INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS group_invites (
                InviteId INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId TEXT NOT NULL,
                FromUserId TEXT NOT NULL,
                ToUserId TEXT NOT NULL,
                Status INTEGER NOT NULL DEFAULT 0,
                CreatedAtMs INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS messages (
                Seq INTEGER PRIMARY KEY AUTOINCREMENT,
                FromUserId TEXT NOT NULL,
                FromName TEXT NOT NULL,
                ConvType INTEGER NOT NULL,
                ConvA TEXT,
                ConvB TEXT,
                GroupId TEXT,
                Text TEXT NOT NULL,
                Time TEXT NOT NULL,
                Type INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS conv_reads (
                UserId TEXT NOT NULL,
                ConvKey TEXT NOT NULL,
                LastReadSeq INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (UserId, ConvKey));
            CREATE TABLE IF NOT EXISTS user_settings (
                UserId TEXT NOT NULL,
                Key TEXT NOT NULL,
                Value TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (UserId, Key));
            CREATE TABLE IF NOT EXISTS unread_counts (
                UserId TEXT NOT NULL,
                ConvKey TEXT NOT NULL,
                Count INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (UserId, ConvKey));
            """;
        create.ExecuteNonQuery();

        // 老库增量补列(新库列已建全,此处全部跳过;每个调用幂等)
        EnsureColumn(conn, "users", "IsBanned", "ALTER TABLE users ADD COLUMN IsBanned INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(conn, "users", "PasswordPlain", "ALTER TABLE users ADD COLUMN PasswordPlain TEXT NOT NULL DEFAULT '';");
        EnsureColumn(conn, "users", "PetSize", "ALTER TABLE users ADD COLUMN PetSize REAL NOT NULL DEFAULT 200;");
        EnsureColumn(conn, "groups", "Num", "ALTER TABLE groups ADD COLUMN Num TEXT NOT NULL DEFAULT '';");
        EnsureColumn(conn, "groups", "AdminIds", "ALTER TABLE groups ADD COLUMN AdminIds TEXT NOT NULL DEFAULT '';");
        EnsureColumn(conn, "messages", "Type", "ALTER TABLE messages ADD COLUMN Type INTEGER NOT NULL DEFAULT 0;");

        // 公共群固定群号 000000(老库空号/补的 '1' 一并收敛);新群从 000001 依次递增
        using (var pub = conn.CreateCommand())
        {
            pub.CommandText = "INSERT OR IGNORE INTO groups (GroupId, Name, OwnerId, Num, CreatedAtMs) VALUES ($id, '公共群聊', '', '000000', 0);";
            pub.Parameters.AddWithValue("$id", PublicGroupId);
            pub.ExecuteNonQuery();
        }
        using (var num = conn.CreateCommand())
        {
            num.CommandText = "UPDATE groups SET Num = '000000' WHERE GroupId = $pub AND Num != '000000';";
            num.Parameters.AddWithValue("$pub", PublicGroupId);
            num.ExecuteNonQuery();
        }
        // 老库空号群逐个补号(公共群除外);正常库无空号,直接跳过
        using (var backfill = conn.CreateCommand())
        {
            backfill.CommandText = "SELECT GroupId FROM groups WHERE Num = '' AND GroupId != $pub;";
            backfill.Parameters.AddWithValue("$pub", PublicGroupId);
            using var reader = backfill.ExecuteReader();
            while (reader.Read())
            {
                using var assign = conn.CreateCommand();
                assign.CommandText = "UPDATE groups SET Num = $n WHERE GroupId = $g;";
                assign.Parameters.AddWithValue("$n", NextGroupNum(conn));
                assign.Parameters.AddWithValue("$g", reader.GetString(0));
                assign.ExecuteNonQuery();
            }
        }

        // 迁移完成标记版本(PRAGMA 不支持参数绑定,SchemaVersion 是代码常量,直接插值);
        // 中途失败(未写版本)下次重跑,所有步骤幂等
        using (var set = conn.CreateCommand())
        {
            set.CommandText = $"PRAGMA user_version = {SchemaVersion};";
            set.ExecuteNonQuery();
        }
    }

    /// <summary>当前 schema 版本(新库/老库迁移后都落在这个版本,启动时不再重复迁移)。</summary>
    private const long SchemaVersion = 2;

    /// <summary>
    /// 分配递增的 6 位群号:当前最大数字群号 + 1,格式 000001 起。
    /// 群号只增不复用(解散的群号不回收),保证历史群号永不冲突。
    /// </summary>
    private static string NextGroupNum(SqliteConnection conn)
    {
        using var max = conn.CreateCommand();
        max.CommandText = "SELECT MAX(CAST(Num AS INTEGER)) FROM groups WHERE Num GLOB '[0-9]*';";
        var cur = max.ExecuteScalar() is long v ? v : 0;
        return (cur + 1).ToString("D6");
    }

    /// <summary>检测列不存在则 ALTER TABLE 补列(增量迁移,不丢数据)。</summary>
    private static void EnsureColumn(SqliteConnection conn, string table, string column, string alterSql)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c;";
            check.Parameters.AddWithValue("$c", column);
            if ((long)check.ExecuteScalar()! > 0)
                return;
        }
        using var alter = conn.CreateCommand();
        alter.CommandText = alterSql;
        alter.ExecuteNonQuery();
    }

}
