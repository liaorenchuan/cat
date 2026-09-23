using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace QPet.Core;

/// <summary>
/// 局域网客户端(双端共用):WebSocket 实时双向(毫秒级)。
/// 一条连接承载 IM(认证/好友/群聊);断线指数退避自动重连(1s→30s),
/// 重连后 welcome 携带消息游标,差异走 GET /api/messages 补拉。
/// 所有事件切回 UI 线程触发(构造注入的调度器;不注入则调用线程直接触发)。
/// </summary>
public sealed class SyncClient
{
    /// <summary>心跳间隔(服务器 12s 判定掉线,3s 打卡余量充足)。</summary>
    private const int PingIntervalMs = 3_000;

    /// <summary>缓存镜像消息上限(与 ChatView.MaxMessages / LocalCacheStore 对齐)。</summary>
    private const int MaxCachedMessages = 5000;

    private const int BackoffBaseMs = 1_000;
    private const int BackoffMaxMs = 30_000;

    private readonly Action<Action>? _dispatchToUI; // UI 线程调度器(WPF Dispatcher)
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly SemaphoreSlim _sendGate = new(1, 1); // WebSocket 不能并发发送
    private readonly SemaphoreSlim _pullGate = new(1, 1); // 历史补拉串行:登录瞬间连接循环与主界面会各发起一次全量,并发会互相抢游标
    private bool _fullPulled; // 本会话已完成过一次 since=0 全量补拉(再来直接复用结果,不重复下载一遍)
    private CancellationTokenSource? _cts;
    private ClientWebSocket? _ws;
    private string _account = "";
    private string _password = "";
    private string _nickname = "";
    private string _authMode = "auth"; // "auth" 或 "register"(注册成功后自动登录)
    private string _wsUrl = "";
    private string _httpUrl = ""; // 补拉历史用的 http 基址
    private bool _authDead;       // 认证失败:凭据无效,停止重连(等用户重新提交)

    /// <summary>收到一条聊天消息:实时推送或重连补拉(UI 线程)。</summary>
    public event Action<ChatMessage>? ChatReceived;

    /// <summary>收到已读回执:会话中某人已读到某 Seq(我的消息被对方读了)。UI 线程。</summary>
    public event Action<ReadReceipt>? ReadReceived;

    /// <summary>welcome 快照里的已读游标(重连/重启后初始化用)。UI 线程。</summary>
    public event Action<List<ReadState>>? ReadStatesReceived;

    /// <summary>收到在线用户列表(UI 线程)。</summary>
    public event Action<List<UserInfo>>? UsersReceived;

    /// <summary>认证成功(拿到服务器身份)。</summary>
    public event Action<UserInfo>? AuthOk;

    /// <summary>认证失败(账号/密码错误等,中文原因)。</summary>
    public event Action<string>? AuthFailed;

    /// <summary>welcome 全量快照(好友/群/申请)。</summary>
    public event Action<AuthSnapshot>? WelcomeReceived;

    /// <summary>好友搜索结果。UI 线程。</summary>
    public event Action<List<UserSearchResult>>? SearchResultsReceived;

    /// <summary>用户资料(群聊点头像查看)。UI 线程。</summary>
    public event Action<UserProfile>? UserProfileReceived;

    /// <summary>收到新好友申请(推给被申请人)。UI 线程。</summary>
    public event Action<FriendRequest>? FriendRequestReceived;

    /// <summary>我的好友申请被处理(同意/拒绝):requestId, accepted, fromUserId, fromName。</summary>
    public event Action<long, bool, string, string>? FriendRequestResultReceived;

    /// <summary>我审批的申请已被处理,从待审批列表移除。UI 线程。</summary>
    public event Action<long>? RequestHandled; // requestId

    /// <summary>成为好友(双方刷新列表)。UI 线程。</summary>
    public event Action<FriendInfo>? FriendAdded;

    /// <summary>被删好友(双方刷新列表,参数 = 被删好友 UserId)。UI 线程。</summary>
    public event Action<string>? FriendDeleted;

    /// <summary>建群/改名/成员变动(全体成员刷新)。UI 线程。</summary>
    public event Action<GroupInfo>? GroupUpdated;

    /// <summary>群搜索结果(按群名/群号模糊匹配,空列表 = 未找到)。UI 线程。</summary>
    public event Action<List<GroupSearchResult>>? GroupSearchResultReceived;

    /// <summary>收到新加群申请(我是群主/管理员,需要审批)。</summary>
    public event Action<GroupRequest>? GroupRequestReceived;

    /// <summary>我的加群申请被处理(同意/拒绝):requestId, accepted, groupId, groupName。</summary>
    public event Action<long, bool, string, string>? GroupRequestResultReceived;

    /// <summary>我审批过的申请已处理,从待审批列表移除。</summary>
    public event Action<long>? GroupRequestHandled;

    /// <summary>收到新群邀请(有人拉我进群,需要我同意)。</summary>
    public event Action<GroupInvite>? GroupInviteReceived;

    /// <summary>我发出的群邀请被处理(同意/拒绝):inviteId, accepted, groupId, groupName。</summary>
    public event Action<long, bool, string, string>? GroupInviteResultReceived;

    /// <summary>我收到的邀请已处理,从待同意列表移除。</summary>
    public event Action<long>? GroupInviteHandled;

    /// <summary>群被解散或我被移出,参数 = (群 ID, 原因;解散时原因为空)。UI 线程。</summary>
    public event Action<string, string>? GroupRemoved; // groupId, reason

    /// <summary>好友昵称变更。UI 线程。</summary>
    public event Action<string, string>? ProfileUpdated;

    /// <summary>云端宠物缩放尺寸(登录下发 / 其他端修改后推送)。UI 线程。</summary>
    public event Action<double>? PetSizeReceived;

    /// <summary>云端键值设置更新(welcome 下发 / 同账号其他端修改推送)。UI 线程。</summary>
    public event Action<string, string>? SettingReceived;

    /// <summary>未读红点更新(消息送达 +1 / 打开会话归零,服务器权威)。UI 线程。</summary>
    public event Action<string, int>? UnreadUpdated;

    /// <summary>一次历史补拉完成(首次全量/重连增量,含失败兜底),聊天页据此整体重算未读。UI 线程。</summary>
    public event Action? HistoryLoaded;

    /// <summary>历史补拉重试后仍失败(参数 = 中文原因):界面据此提示"记录没拉到",而不是假装加载成功。UI 线程。</summary>
    public event Action<string>? HistoryLoadFailed;

    /// <summary>一般性错误(发送被拒等)。UI 线程。</summary>
    public event Action<string>? ErrorReceived;

    public SyncClient(Action<Action>? dispatchToUI = null)
    {
        _dispatchToUI = dispatchToUI;
    }

    /// <summary>诊断输出:安卓进 logcat、桌面进调试窗口(不引平台 API,保持双端共用)。</summary>
    private static void Log(string message) => System.Diagnostics.Debug.WriteLine($"[QPET] {message}");

    public bool IsConnected { get; private set; }

    /// <summary>已见消息游标(断线补拉基准)。</summary>
    private long LastSeq { get; set; }

    /// <summary>当前登录身份(认证成功后有值)。</summary>
    public UserInfo MyUser { get; private set; } = new();

    /// <summary>认证时下发的会话令牌(/api/messages 补拉历史用,服务端校验)。</summary>
    private string Token { get; set; } = "";

    /// <summary>最近一次 welcome 全量快照(好友/群/申请),页面打开时初始化用。</summary>
    public AuthSnapshot LastSnapshot { get; private set; } = new();

    /// <summary>本地离线缓存镜像:在线时随帧持续同步(身份/快照/消息/游标),App 决定落盘时机。</summary>
    public LocalCache Cache { get; private set; } = new();

    /// <summary>离线模式:不建立连接,身份/快照来自本地缓存,IsConnected 恒为 false。</summary>
    public bool IsOffline { get; private set; }

    /// <summary>进入离线模式:停掉连接,注入缓存身份与快照。之后所有 Send* 经 SendJson 静默丢弃。</summary>
    public void EnterOfflineMode(LocalCache cache)
    {
        Stop();
        IsOffline = true;
        Cache = cache;
        MyUser = cache.MyUser;
        LastSnapshot = cache.Snapshot;
        LastSeq = cache.LastSeq;
        _account = cache.Account;
    }


    /// <summary>
    /// 登录连接(自动重连)。地址支持 http://192.168.x.x:54321、ws://192.168.x.x:54321/ws、裸 IP 三种。
    /// </summary>
    public void Start(string url, string account, string password)
    {
        StartCore(url, "auth", account, password, "");
    }

    /// <summary>注册连接(成功后服务器回 authOk 自动登录)。</summary>
    public void StartRegister(string url, string account, string password, string nickname)
    {
        StartCore(url, "register", account, password, nickname);
    }

    private void StartCore(string url, string mode, string account, string password, string nickname)
    {
        Stop();

        _wsUrl = ToWsUrl(url);
        _httpUrl = ToHttpUrl(_wsUrl);
        _authMode = mode;
        _account = account;
        _password = password;
        _nickname = nickname;
        _authDead = false;
        LastSnapshot = new AuthSnapshot(); // 换账号:旧账号快照必须清,UI 不能再读到旧列表
        IsOffline = false;
        Cache = new LocalCache();          // 同理:旧账号缓存镜像清空,防止误落盘
        LastSeq = 0;                       // 换账号必须重置游标:沿用上个账号的游标会让新账号的历史补拉从半路开始
        Token = "";                        // 上个账号的会话令牌对新账号无效
        _fullPulled = false;               // 新会话:重新允许一次全量补拉
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectionLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _ws?.Dispose();
        _ws = null;
        if (IsConnected)
            IsConnected = false;
    }

    /// <summary>中止本次登录尝试(登录等待超时):断开重试并把可能已到达的身份清干净。
    /// 只解绑事件、不断连接的话,迟到的 authOk 会把状态搞成"人停在登录页、却已经算登录" ——
    /// MyUser 有值而账号名为空,缓存还会因为账号为空整场都不落盘。</summary>
    public void AbortSession()
    {
        Stop();
        MyUser = new UserInfo();
        LastSnapshot = new AuthSnapshot();
        Cache = new LocalCache();
        LastSeq = 0;
        Token = "";
        _fullPulled = false;
        _authDead = false;
        IsOffline = false;
    }

    /// <summary>退出登录:通知服务器下线并断开(不再重连)。</summary>
    public void Logout()
    {
        if (_ws is not null)
        {
            try { SendJson(new { t = Protocol.Logout }); } catch { }
        }
        Stop();
        MyUser = new UserInfo();
        IsOffline = false;
        Cache = new LocalCache();
    }

    // ---- IM 发送 ----

    /// <summary>发消息:convType 0=私聊(convId 对方 UserId) 1=群聊(convId 群 ID)。断开时静默丢弃。</summary>
    public void SendChat(int convType, string convId, string text)
    {
        SendJson(new { t = Protocol.Chat, convType, convId, text });
    }

    /// <summary>已读上报:本会话已读到 seq(打开会话/收到新消息时发;服务器落库并推给会话其他人)。</summary>
    public void SendRead(int convType, string convId, long seq)
    {
        SendJson(new { t = Protocol.Read, convType, convId, seq });
    }

    /// <summary>按账号/昵称搜索用户。</summary>
    public void SearchFriends(string keyword) =>
        SendJson(new { t = Protocol.FriendSearch, keyword });

    /// <summary>按 UserId 查用户资料(昵称/账号/好友状态),结果走 UserProfileReceived。</summary>
    public void GetUserProfile(string targetUserId) =>
        SendJson(new { t = Protocol.UserProfile, targetUserId });

    /// <summary>发送好友申请。</summary>
    /// <summary>删除好友(双方关系同时删除;服务器会推 friendDeleted 给双方)。</summary>
    public void SendDeleteFriend(string targetUserId) =>
        SendJson(new { t = Protocol.FriendDelete, targetUserId });

    public void SendFriendRequest(string targetUserId, string text) =>
        SendJson(new { t = Protocol.FriendRequest, targetUserId, text });

    /// <summary>处理好友申请。</summary>
    public void RespondFriendRequest(long requestId, bool accept) =>
        SendJson(new { t = Protocol.FriendRespond, requestId, accept });

    /// <summary>设置好友备注(空 = 取消)。</summary>
    public void SetFriendRemark(string targetUserId, string remark) =>
        SendJson(new { t = Protocol.FriendRemark, targetUserId, remark });

    /// <summary>创建群聊(成员 = 自己 + memberIds)。</summary>
    public void CreateGroup(string name, List<string> memberIds) =>
        SendJson(new { t = Protocol.GroupCreate, name, memberIds });

    /// <summary>群改名。</summary>
    public void RenameGroup(string groupId, string name) =>
        SendJson(new { t = Protocol.GroupRename, groupId, name });

    /// <summary>按群名或群号搜索群(结果走 GroupSearchResultReceived)。</summary>
    public void SearchGroup(string keyword) =>
        SendJson(new { t = Protocol.GroupSearch, keyword });

    /// <summary>加入群聊(公共群;成功由 groupUpdated 推送刷新)。</summary>
    public void JoinGroup(string groupId) =>
        SendJson(new { t = Protocol.GroupJoin, groupId });

    /// <summary>退出群聊(群主退出 = 解散,全员收 groupRemoved)。</summary>
    public void LeaveGroup(string groupId) =>
        SendJson(new { t = Protocol.GroupLeave, groupId });

    /// <summary>发送加群申请(结果走 GroupRequestResultReceived)。</summary>
    public void SendGroupRequest(string groupId) =>
        SendJson(new { t = Protocol.GroupRequestSend, groupId });

    /// <summary>审批加群申请(仅群主/管理员;同意后对方自动入群)。</summary>
    public void RespondGroupRequest(long requestId, bool accept) =>
        SendJson(new { t = Protocol.GroupRequestRespond, requestId, accept });

    /// <summary>发群邀请拉好友进群(须是自己好友;对方同意后加入,结果走 GroupInviteResultReceived)。</summary>
    public void SendGroupInvite(string groupId, string targetUserId) =>
        SendJson(new { t = Protocol.GroupInviteSend, groupId, targetUserId });

    /// <summary>审批群邀请(同意 = 入群;拒绝 = 通知发起者)。帧字段须为 requestId(服务器 WsFrame 约定)。</summary>
    public void RespondGroupInvite(long inviteId, bool accept) =>
        SendJson(new { t = Protocol.GroupInviteRespond, requestId = inviteId, accept });

    /// <summary>群主设置/取消管理员。</summary>
    public void SetGroupAdmin(string groupId, string targetUserId, bool isAdmin) =>
        SendJson(new { t = Protocol.GroupAdminSet, groupId, targetUserId, accept = isAdmin });

    /// <summary>群主把成员移出群聊(剩余成员收 groupUpdated,被移者收 groupRemoved)。</summary>
    public void RemoveGroupMember(string groupId, string targetUserId) =>
        SendJson(new { t = Protocol.GroupRemoveMember, groupId, targetUserId });

    /// <summary>修改自己的昵称。</summary>
    public void UpdateNickname(string nickname) =>
        SendJson(new { t = Protocol.ProfileUpdate, nickname });

    /// <summary>上传宠物缩放尺寸到云端(服务器写库并推给本账号全部连接)。</summary>
    public void UpdatePetSize(double petSize) =>
        SendJson(new { t = Protocol.PetSizeUpdate, petSize });

    /// <summary>云端键值设置:服务器写库并回推本账号全部连接(换设备登录也有)。</summary>
    public void SetSetting(string key, string value) =>
        SendJson(new { t = Protocol.SetSetting, key, value });

    /// <summary>拉取可见历史(全量,seq 0 起),逐条触发 ChatReceived。聊天页打开时调用。</summary>
    public Task LoadHistoryAsync() =>
        PullMissedAsync(0, CancellationToken.None);

    // ---- 连接循环 ----

    private async Task ConnectionLoop(CancellationToken ct)
    {
        var backoff = BackoffBaseMs;
        while (!ct.IsCancellationRequested && !_authDead)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(_wsUrl), ct);
                _ws = ws;
                SetConnected(true);

                // 认证握手:发 register/auth,等 authOk(成功)或 error(失败,停止重连)
                object payload;
                if (_authMode == "register")
                    payload = new { t = Protocol.Register, account = _account, password = _password, nickname = _nickname };
                else
                    payload = new { t = Protocol.Auth, account = _account, password = _password };
                await SendRawAsync(ws, payload, ct);

                var authed = false;
                // 握手带超时:服务器半开(连上不回帧)时不能无限等,10 秒后按连接失败退避重连
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshakeCts.CancelAfter(10_000);
                for (var i = 0; i < 8; i++)
                {
                    var root = await ReceiveOne(ws, handshakeCts.Token);
                    switch (root.GetProperty("t").GetString())
                    {
                        case Protocol.AuthOk:
                            MyUser = new UserInfo
                            {
                                UserId = root.GetProperty("userId").GetString() ?? "",
                                Nickname = root.GetProperty("nickname").GetString() ?? "",
                            };
                            Token = root.TryGetProperty("token", out var t0) ? t0.GetString() ?? "" : "";
                            // 登录下发云端宠物尺寸(老服务器无该字段 = 默认 200)
                            var authSize = root.TryGetProperty("petSize", out var ps0) ? ps0.GetDouble() : PetConfig.DefaultPetSize;
                            authed = true;
                            Cache.MyUser = MyUser;
                            Fire(() => AuthOk?.Invoke(MyUser));
                            Fire(() => PetSizeReceived?.Invoke(authSize));
                            break;

                        case Protocol.Error:
                            var reason = root.GetProperty("reason").GetString() ?? "登录失败";
                            if (!authed)
                            {
                                // 认证失败:通知 UI,停止重连(错误凭据重试无意义,等用户重新提交)
                                _authDead = true;
                                Fire(() => AuthFailed?.Invoke(reason));
                                throw new WebSocketException("auth failed: " + reason);
                            }
                            Fire(() => ErrorReceived?.Invoke(reason));
                            break;

                        default:
                            break; // users/state 等握手期帧,跳过
                    }
                    if (authed)
                        break; // 拿到 authOk 即继续,welcome 等后续帧交给 ReceiveLoop
                }
                if (!authed)
                    throw new WebSocketException("no authOk frame"); // 交给外层重连

                backoff = BackoffBaseMs; // 连上即复位退避,避免下次重连还按旧退避等待

                // 断线期间漏掉的消息:按游标补拉(补拉失败忽略,实时推送会继续推进游标)
                await PullMissedAsync(LastSeq, ct);

                // 心跳与接收并行;任一结束 = 连接断开,进入重连退避
                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pingTask = Task.Run(() => PingLoopAsync(ws, pingCts.Token), ct);
                try
                {
                    await ReceiveLoopAsync(ws, ct);
                }
                finally
                {
                    pingCts.Cancel();
                    try { await pingTask; } catch { }
                }
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                    break; // Stop() 或页面关闭
                // 握手超时等非主动取消:按连接失败处理,退避重连
            }
            catch
            {
                // 连接失败 / 中途断开 / 认证失败:退避后重连
            }

            SetConnected(false);
            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { break; }
            backoff = Math.Min(backoff * 2, BackoffMaxMs);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            string? json;
            try
            {
                // 必须收满一整帧:WebSocket 不保证一次 ReceiveAsync 收完一条消息,
                // 服务器的大帧(welcome 全量快照 = 好友+全部群+群成员)超缓冲区就得分多次收,
                // 只收一次会把 JSON 截断 → 解析失败 → 整帧丢(界面全空且一声不吭)。
                json = await ReceiveFullAsync(ws, buffer, ct);
            }
            catch (OperationCanceledException)
            {
                break; // Stop() 或页面关闭
            }
            catch (Exception ex)
            {
                Log($"接收失败,按断开处理: {ex.GetType().Name} - {ex.Message}");
                break; // 连接不可用:交给外层退避重连
            }
            if (json is null)
                break;     // 对端关闭
            if (json.Length == 0)
                continue;  // 非文本帧:忽略

            try
            {
                HandleFrame(json);
            }
            catch (Exception ex)
            {
                // 单帧解析失败不影响连接,但必须留痕(以前是纯吞:大帧被截断时毫无线索)
                Log($"帧解析失败: {ex.GetType().Name} - {ex.Message}");
            }
        }
    }

    private void HandleFrame(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        switch (root.GetProperty("t").GetString())
        {
            case Protocol.Chat:
                var msg = JsonSerializer.Deserialize<ChatMessage>(json, JsonOpts.Web);
                if (msg is not null)
                {
                    if (msg.Seq > LastSeq)
                        LastSeq = msg.Seq; // 补拉游标(后台线程自用):宁可偏保守,靠下面的去重兜底
                    // 缓存镜像只在 UI 线程改:与界面遍历同一线程,不会"边加边遍历"(安卓上的闪退来源)
                    Fire(() =>
                    {
                        if (msg.Seq > Cache.LastSeq)
                            Cache.LastSeq = msg.Seq;
                        // 已在缓存里(补拉与实时推送重叠)就不再发事件:界面不会出现两个一样的气泡
                        if (AppendCachedMessage(msg))
                            ChatReceived?.Invoke(msg);
                    });
                }
                break;

            case Protocol.Users:
                var users = JsonSerializer.Deserialize<List<UserInfo>>(
                    root.GetProperty("users").GetRawText(), JsonOpts.Web);
                if (users is not null)
                    Fire(() => UsersReceived?.Invoke(users));
                break;

            case Protocol.Welcome:
                // 认证握手后由 ReceiveLoop 收到的全量快照(好友/群/申请 + 游标)
                // 游标只进不退:补拉/实时推送可能已推进到快照游标之后,不能回退
                var welcomeSeq = root.GetProperty("seq").GetInt64();
                if (welcomeSeq > LastSeq)
                    LastSeq = welcomeSeq;
                var friends = JsonSerializer.Deserialize<List<FriendInfo>>(
                    root.GetProperty("friends").GetRawText(), JsonOpts.Web) ?? new List<FriendInfo>();
                var groups = JsonSerializer.Deserialize<List<GroupInfo>>(
                    root.GetProperty("groups").GetRawText(), JsonOpts.Web) ?? new List<GroupInfo>();
                var requests = JsonSerializer.Deserialize<List<FriendRequest>>(
                    root.GetProperty("requests").GetRawText(), JsonOpts.Web) ?? new List<FriendRequest>();
                var groupRequests = JsonSerializer.Deserialize<List<GroupRequest>>(
                    root.TryGetProperty("groupRequests", out var grEl)
                        ? grEl.GetRawText() : "[]", JsonOpts.Web) ?? new List<GroupRequest>();
                // 我收到的群邀请(被拉进群待同意);旧服务器没有该字段时为空
                var groupInvites = JsonSerializer.Deserialize<List<GroupInvite>>(
                    root.TryGetProperty("groupInvites", out var giEl)
                        ? giEl.GetRawText() : "[]", JsonOpts.Web) ?? new List<GroupInvite>();
                var readStates = JsonSerializer.Deserialize<List<ReadState>>(
                    root.TryGetProperty("readStates", out var rsEl)
                        ? rsEl.GetRawText() : "[]", JsonOpts.Web) ?? new List<ReadState>();
                // 云端键值设置(群备注/未读游标/宠物参数等),旧服务器没有该字段时为空
                var settings = new Dictionary<string, string>();
                if (root.TryGetProperty("settings", out var stEl))
                {
                    foreach (var p in stEl.EnumerateObject())
                        settings[p.Name] = p.Value.GetString() ?? "";
                }
                // 未读红点(服务器权威计数),旧服务器没有该字段时为空
                var unreadCounts = new Dictionary<string, int>();
                if (root.TryGetProperty("unreadCounts", out var ucEl))
                {
                    foreach (var p in ucEl.EnumerateObject())
                        unreadCounts[p.Name] = p.Value.GetInt32();
                }
                LastSnapshot = new AuthSnapshot
                {
                    Friends = friends,
                    Groups = groups,
                    Requests = requests,
                    GroupRequests = groupRequests,
                    GroupInvites = groupInvites,
                    ReadStates = readStates,
                    Settings = settings,
                    UnreadCounts = unreadCounts,
                };
                Cache.Snapshot = LastSnapshot;
                Cache.LastSeq = LastSeq;
                Fire(() => WelcomeReceived?.Invoke(LastSnapshot));
                Fire(() => ReadStatesReceived?.Invoke(readStates));
                break;

            case Protocol.Read:
                // 已读回执:会话中某人已读到 seq(我的消息被对方/群成员读了)
                var receipt = JsonSerializer.Deserialize<ReadReceipt>(json, JsonOpts.Web);
                if (receipt is not null)
                    Fire(() => ReadReceived?.Invoke(receipt));
                break;

            case Protocol.FriendSearchResult:
                var results = JsonSerializer.Deserialize<List<UserSearchResult>>(
                    root.GetProperty("users").GetRawText(), JsonOpts.Web);
                if (results is not null)
                    Fire(() => SearchResultsReceived?.Invoke(results));
                break;

            case Protocol.UserProfileResult:
                var profile = JsonSerializer.Deserialize<UserProfile>(
                    root.GetProperty("profile").GetRawText(), JsonOpts.Web);
                if (profile is not null)
                    Fire(() => UserProfileReceived?.Invoke(profile));
                break;

            case Protocol.FriendRequestReceived:
                var req = JsonSerializer.Deserialize<FriendRequest>(
                    root.GetProperty("request").GetRawText(), JsonOpts.Web);
                if (req is not null)
                {
                    // 快照同步(群申请/群邀请同款):申请中心列表从快照读,在线收到的申请不落快照会"红点有、列表空"
                    LastSnapshot.Requests = LastSnapshot.Requests.Append(req).ToList();
                    Fire(() => FriendRequestReceived?.Invoke(req));
                }
                break;

            case Protocol.FriendRequestResult:
                // 我的好友申请被处理(同意/拒绝):提示;同意时 FriendAdded 会刷好友列表
                var frReqId = root.GetProperty("requestId").GetInt64();
                var frAccepted = root.GetProperty("accepted").GetBoolean();
                var frFromUserId = root.GetProperty("fromUserId").GetString() ?? "";
                var frFromName = root.GetProperty("fromName").GetString() ?? "";
                Fire(() => FriendRequestResultReceived?.Invoke(frReqId, frAccepted, frFromUserId, frFromName));
                break;

            case Protocol.FriendRequestHandled:
                // 我审批的申请已被处理:从快照待处理列表移除,UI 刷徽章/弹窗
                var handledId = root.GetProperty("requestId").GetInt64();
                LastSnapshot.Requests = LastSnapshot.Requests.Where(r => r.ReqId != handledId).ToList();
                Fire(() => RequestHandled?.Invoke(handledId));
                break;

            case Protocol.FriendAdded:
                var friend = JsonSerializer.Deserialize<FriendInfo>(
                    root.GetProperty("friend").GetRawText(), JsonOpts.Web);
                if (friend is not null)
                {
                    // 快照同步更新(页面重建时还能看到新好友);整体替换列表防跨线程枚举冲突
                    var fs = LastSnapshot.Friends.Where(f => f.UserId != friend.UserId).ToList();
                    fs.Add(friend);
                    LastSnapshot.Friends = fs;
                    Fire(() => FriendAdded?.Invoke(friend));
                }
                break;

            case Protocol.FriendDeleted:
                var deletedId = root.GetProperty("userId").GetString() ?? "";
                LastSnapshot.Friends = LastSnapshot.Friends.Where(f => f.UserId != deletedId).ToList();
                Fire(() => FriendDeleted?.Invoke(deletedId));
                break;

            case Protocol.GroupUpdated:
                var group = JsonSerializer.Deserialize<GroupInfo>(
                    root.GetProperty("group").GetRawText(), JsonOpts.Web);
                if (group is not null)
                {
                    // 快照同步更新(新群/改名/成员变动后重建页面也能拿到最新数据)
                    var gs = LastSnapshot.Groups.Where(g => g.GroupId != group.GroupId).ToList();
                    gs.Add(group);
                    LastSnapshot.Groups = gs;
                    Fire(() => GroupUpdated?.Invoke(group));
                }
                break;

            case Protocol.GroupSearchResult:
                // groups = 群名/群号模糊匹配结果列表(空 = 未找到)
                var searchGroups = root.TryGetProperty("groups", out var gsEl)
                    ? JsonSerializer.Deserialize<List<GroupSearchResult>>(gsEl.GetRawText(), JsonOpts.Web) ?? new()
                    : new List<GroupSearchResult>();
                Fire(() => GroupSearchResultReceived?.Invoke(searchGroups));
                break;

            case Protocol.GroupRemoved:
                var removedGroupId = root.GetProperty("groupId").GetString() ?? "";
                var removedReason = root.TryGetProperty("reason", out var rr) ? rr.GetString() ?? "" : "";
                LastSnapshot.Groups = LastSnapshot.Groups.Where(g => g.GroupId != removedGroupId).ToList();
                Fire(() => GroupRemoved?.Invoke(removedGroupId, removedReason));
                break;

            case Protocol.GroupRequestReceived:
                var grReq = JsonSerializer.Deserialize<GroupRequest>(
                    root.GetProperty("request").GetRawText(), JsonOpts.Web);
                if (grReq is not null)
                {
                    LastSnapshot.GroupRequests = LastSnapshot.GroupRequests.Append(grReq).ToList();
                    Fire(() => GroupRequestReceived?.Invoke(grReq));
                }
                break;

            case Protocol.GroupRequestResult:
                // 我的加群申请被处理:从快照待审批列表移除,UI 提示结果
                var grResult = JsonSerializer.Deserialize<GroupRequest>(
                    root.GetProperty("request").GetRawText(), JsonOpts.Web);
                var grAccepted = root.GetProperty("accepted").GetBoolean();
                if (grResult is not null)
                    Fire(() => GroupRequestResultReceived?.Invoke(
                        grResult.ReqId, grAccepted, grResult.GroupId, grResult.GroupName));
                break;

            case Protocol.GroupRequestHandled:
                // 我审批的申请已被处理:从待审批列表移除,UI 刷徽章
                var handledGrId = root.GetProperty("requestId").GetInt64();
                LastSnapshot.GroupRequests = LastSnapshot.GroupRequests.Where(r => r.ReqId != handledGrId).ToList();
                Fire(() => GroupRequestHandled?.Invoke(handledGrId));
                break;

            case Protocol.GroupInviteReceived:
                // 有人拉我进群:进快照待同意列表,UI 红点 +1 / 邀请 Tab 插行
                var giReq = JsonSerializer.Deserialize<GroupInvite>(
                    root.GetProperty("invite").GetRawText(), JsonOpts.Web);
                if (giReq is not null)
                {
                    LastSnapshot.GroupInvites = LastSnapshot.GroupInvites.Append(giReq).ToList();
                    Fire(() => GroupInviteReceived?.Invoke(giReq));
                }
                break;

            case Protocol.GroupInviteResult:
                // 我发出的邀请被处理(同意/拒绝):UI 状态栏提示(同意时 GroupUpdated 会刷群列表)
                var giId = root.GetProperty("inviteId").GetInt64();
                var giAccepted = root.GetProperty("accepted").GetBoolean();
                var giGroupId = root.GetProperty("groupId").GetString() ?? "";
                var giGroupName = root.GetProperty("groupName").GetString() ?? "";
                Fire(() => GroupInviteResultReceived?.Invoke(giId, giAccepted, giGroupId, giGroupName));
                break;

            case Protocol.GroupInviteHandled:
                // 我收到的邀请已被处理(群解散/拒绝):从待同意列表移除,UI 刷徽章
                var handledGiId = root.GetProperty("inviteId").GetInt64();
                LastSnapshot.GroupInvites = LastSnapshot.GroupInvites.Where(i => i.InviteId != handledGiId).ToList();
                Fire(() => GroupInviteHandled?.Invoke(handledGiId));
                break;

            case Protocol.PetSizeUpdated:
                // 同账号其他端改了宠物尺寸:推给 UI 实时应用
                var petSize = root.GetProperty("petSize").GetDouble();
                Fire(() => PetSizeReceived?.Invoke(petSize));
                break;

            case Protocol.SettingUpdated:
                // 云端键值设置(本人任意端写入的回推):同步进快照缓存,UI 订阅更新
                var sKey = root.GetProperty("key").GetString() ?? "";
                var sValue = root.GetProperty("value").GetString() ?? "";
                LastSnapshot.Settings[sKey] = sValue;
                Fire(() => SettingReceived?.Invoke(sKey, sValue));
                break;

            case Protocol.UnreadUpdate:
                // 未读红点(消息送达 +1 / 打开会话归零):同步进快照缓存,UI 刷新红点
                var ucKey = root.GetProperty("convKey").GetString() ?? "";
                var ucCount = root.GetProperty("count").GetInt32();
                LastSnapshot.UnreadCounts[ucKey] = ucCount;
                Fire(() => UnreadUpdated?.Invoke(ucKey, ucCount));
                break;

            case Protocol.ProfileUpdated:
                // 闭包在 UI 线程延迟执行,取值必须在 using 块内先提取,避免访问已释放的 JsonDocument
                var pUserId = root.GetProperty("userId").GetString() ?? "";
                var pNickname = root.GetProperty("nickname").GetString() ?? "";
                // 好友昵称同步进快照(新页面打开显示最新备注名)
                var pfs = LastSnapshot.Friends.ToList();
                var pTarget = pfs.FirstOrDefault(f => f.UserId == pUserId);
                if (pTarget is not null)
                {
                    pTarget.Nickname = pNickname;
                    LastSnapshot.Friends = pfs;
                }
                Fire(() => ProfileUpdated?.Invoke(pUserId, pNickname));
                break;

            case Protocol.Error:
                var errReason = root.GetProperty("reason").GetString() ?? "";
                Fire(() => ErrorReceived?.Invoke(errReason));
                break;
        }
    }

    /// <summary>心跳:25s 一次 ping,服务器以此刷新在线状态。</summary>
    private async Task PingLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(PingIntervalMs, ct);
            if (ws.State != WebSocketState.Open)
                break;
            await SendRawAsync(ws, new { t = Protocol.Ping }, ct);
        }
    }

    /// <summary>按游标补拉消息:连接时拉断线增量,聊天页打开时拉全量。
    /// 串行执行(同一时刻只跑一次)+ 失败自动重试;重试仍失败会发 HistoryLoadFailed,界面不再"假装加载成功"。</summary>
    private async Task PullMissedAsync(long since, CancellationToken ct)
    {
        // 握手期 Token 未下发:直接跳过,不发 HistoryLoaded(聊天页此时还未就绪)
        if (Token.Length == 0)
            return;

        await _pullGate.WaitAsync(ct);
        try
        {
            // 本会话已经全量拉过一遍:直接复用(登录瞬间连接循环与主界面会各发起一次 since=0,
            // 并发跑会互相抢游标、把对方的消息判成重复丢掉,白下载两遍)
            if (since == 0 && _fullPulled)
                return;

            var ok = false;
            for (var attempt = 1; attempt <= 3 && !ok; attempt++)
            {
                if (ct.IsCancellationRequested)
                    return;
                try
                {
                    // 用认证下发的会话令牌换身份(服务端校验),不用可被冒用的裸 UserId
                    using var resp = await _http.GetAsync(
                        $"{_httpUrl}/api/messages?since={since}&token={Uri.EscapeDataString(Token)}", ct);
                    resp.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    var msgs = JsonSerializer.Deserialize<List<ChatMessage>>(
                        doc.RootElement.GetProperty("messages").GetRawText(), JsonOpts.Web);
                    if (msgs is null)
                    {
                        ok = true; // 服务器返回空:按"无增量"处理
                        break;
                    }

                    foreach (var m in msgs)
                    {
                        if (m.Seq > LastSeq)
                            LastSeq = m.Seq;
                        // 与实时推送同款:缓存写入与事件都放 UI 线程,重复消息不重复上屏
                        Fire(() =>
                        {
                            if (m.Seq > Cache.LastSeq)
                                Cache.LastSeq = m.Seq;
                            if (AppendCachedMessage(m))
                                ChatReceived?.Invoke(m);
                        });
                    }
                    ok = true;
                    if (since == 0)
                        _fullPulled = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return; // 主动取消(Stop/关闭):不复位也不报错
                }
                catch (Exception ex)
                {
                    Log($"历史补拉失败(第 {attempt}/3 次): {ex.GetType().Name} - {ex.Message}");
                    if (attempt < 3)
                    {
                        try { await Task.Delay(2_000, ct); }
                        catch (OperationCanceledException) { return; }
                    }
                }
            }

            if (!ok)
            {
                // 以前这里静默吞掉、还照样通知"加载完成":表现就是登录后聊天记录空白且没有任何提示
                Fire(() => HistoryLoadFailed?.Invoke("聊天记录没拉到,网络恢复后重新进入会话可重试"));
            }
        }
        finally
        {
            _pullGate.Release();
            // 无论成败都通知:聊天页据此结束加载态并重算未读(失败时以现有数据重算)
            Fire(() => HistoryLoaded?.Invoke());
        }
    }

    /// <summary>把消息并入缓存镜像:始终按 Seq 升序、重复丢弃、超上限裁掉最旧。
    /// 返回是否真的新增(重复消息返回 false,调用方据此避免重复上屏)。
    /// 注意不能只跟尾部比就丢:补拉与实时推送交错时消息会乱序到达,尾部已更大并不代表这条是旧的 ——
    /// 直接丢弃会让它永久进不了缓存(游标已推进,以后补拉也取不回来),表现为返回列表再进会话就少几条。</summary>
    private bool AppendCachedMessage(ChatMessage msg)
    {
        var msgs = Cache.Messages;
        if (msgs.Count == 0)
        {
            msgs.Add(msg);
            return true;
        }

        var last = msgs[^1];
        if (msg.Seq > last.Seq)
        {
            msgs.Add(msg);
            TrimCache();
            return true;
        }
        if (msg.Seq == last.Seq)
            return false; // 尾部重复

        // 比尾部旧:二分找位置插入(保持升序),已存在则算重复
        var lo = 0;
        var hi = msgs.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (msgs[mid].Seq < msg.Seq)
                lo = mid + 1;
            else
                hi = mid;
        }
        if (msgs[lo].Seq == msg.Seq)
            return false;
        msgs.Insert(lo, msg);
        TrimCache();
        return true;
    }

    /// <summary>超上限裁掉最旧(裁到上限为止)。</summary>
    private void TrimCache()
    {
        var over = Cache.Messages.Count - MaxCachedMessages;
        if (over > 0)
            Cache.Messages.RemoveRange(0, over);
    }

    // ---- 发送 ----

    /// <summary>UI 线程入口:后台发送,失败静默(接收循环会感知断开)。</summary>
    private void SendJson(object payload)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open)
            return;
        _ = Task.Run(() => SendRawAsync(ws, payload, CancellationToken.None));
    }

    private async Task SendRawAsync(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts.Web);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync(ct);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>收一帧完整文本消息:循环到 EndOfMessage 为止(WebSocket 单条消息可能跨多次 ReceiveAsync)。
    /// 返回 null = 对端关闭;返回空串 = 非文本帧(调用方忽略)。</summary>
    private static async Task<string?> ReceiveFullAsync(ClientWebSocket ws, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text)
                return "";
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    /// <summary>握手期收一帧完整消息:循环到 EndOfMessage,避免大帧(群列表/长历史)被单次 ReceiveAsync 截断。</summary>
    private static async Task<JsonElement> ReceiveOne(ClientWebSocket ws, CancellationToken ct)
    {
        var json = await ReceiveFullAsync(ws, new byte[16 * 1024], ct);
        if (string.IsNullOrEmpty(json))
            throw new WebSocketException("连接被关闭");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ---- 内部 ----

    private void SetConnected(bool value)
    {
        if (IsConnected == value)
            return;
        IsConnected = value;
    }

    /// <summary>把回调切到 UI 线程(构造注入的调度器;未注入则调用线程直接触发)。</summary>
    private void Fire(Action action)
    {
        var dispatch = _dispatchToUI;
        if (dispatch is null)
            action();
        else
            dispatch(action);
    }

    /// <summary>归一化服务器地址为 ws://host:port/ws。</summary>
    private static string ToWsUrl(string raw)
    {
        var url = raw.Trim();
        if (url.Length == 0)
            return "";
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "ws://" + url;

        var uri = new Uri(url);
        var scheme = uri.Scheme == "wss" ? "wss" : "ws";
        var port = uri.IsDefaultPort ? Protocol.DefaultPort : uri.Port;
        return $"{scheme}://{uri.Host}:{port}/ws";
    }

    /// <summary>补拉历史用的 http 基址(不含路径)。</summary>
    private static string ToHttpUrl(string wsUrl)
    {
        var uri = new Uri(wsUrl);
        var scheme = uri.Scheme == "wss" ? "https" : "http";
        return $"{scheme}://{uri.Host}:{uri.Port}";
    }
}
