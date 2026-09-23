using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QPet.Core;

namespace QPet.Server;

/// <summary>
/// 局域网 IM 服务器:内嵌 Kestrel,主通道是 /ws WebSocket(毫秒级实时),
/// 一条连接承载 IM(认证/好友/群聊);GET /api/messages 保留作离线补拉 / 历史兜底;
/// /api/admin/* 是管理 REST(管理密码登录后管账户)。
/// 连接首帧必须是 register(注册)或 auth(登录),认证成功回 authOk + welcome 全量快照。
/// </summary>
public sealed class SyncServer
{
    public const int Port = Protocol.DefaultPort;

    private readonly ChatHub _chatHub;
    private readonly AdminApi _adminApi;
    private readonly int _port;
    private WebApplication? _app;

    // 在线 WebSocket 连接:ws → 认证身份(UserId;空 = 已连接未认证)
    private readonly ConcurrentDictionary<WebSocket, string> _clients = new();

    // 每连接一个发送锁:WebSocket 不支持并发 Send,多线程推送(聊天/用户)必须串行
    private readonly ConcurrentDictionary<WebSocket, object> _sendLocks = new();

    // 会话令牌:token → 认证用户(/api/messages 等 HTTP 兜底接口校验用,防他人冒读聊天记录)
    private readonly ConcurrentDictionary<string, string> _tokens = new();

    // 连接 → 其下发过的令牌(连接结束时精准回收,不影响该用户其他在线连接的令牌)
    private readonly ConcurrentDictionary<WebSocket, string> _wsTokens = new();

    private bool _eventsSubscribed; // 防止重复 Start 时重复订阅 ChatHub 事件

    public SyncServer(ChatHub chatHub, AdminApi adminApi, int port = Port)
    {
        _chatHub = chatHub;
        _adminApi = adminApi;
        _port = port;
    }

    /// <summary>本机局域网 IP(客户端需要填的地址)。</summary>
    public string LocalIp { get; } = GetLocalIp();

    /// <summary>WebSocket 连接地址(管理窗口二维码内容)。</summary>
    public string Endpoint => $"ws://{LocalIp}:{_port}/ws";

    public void Start()
    {
        if (_app is not null)
            return;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{_port}");
        builder.Logging.ClearProviders(); // 日志走控制台打印,不用 ASP.NET 日志
        var app = builder.Build();
        app.UseWebSockets();

        // 主通道:实时双向
        app.MapGet("/ws", HandleWebSocket);

        // 兜底:断线重连后按游标补拉历史(WebSocket welcome 只给最新 seq);
        // 鉴权:必须带认证时下发的会话 token(校验通过才给读,防任意账号冒读);
        // 可见性过滤(仅好友私聊 + 我是成员的群)在 ChatHub.GetMessages 内完成
        app.MapGet("/api/messages", (long? since, string? token) =>
        {
            if (string.IsNullOrEmpty(token) || !_tokens.TryGetValue(token, out var me))
                return Results.Unauthorized();
            var msgs = _chatHub.GetMessages(since ?? 0, me);
            return Results.Json(new { messages = msgs });
        });

        // 管理 REST(管理密码登录 → token → 管账户)
        app.MapPost("/api/admin/login", _adminApi.HandleLogin);
        app.MapGet("/api/admin/users", _adminApi.HandleUsers);
        app.MapPost("/api/admin/createUser", _adminApi.HandleCreateUser);
        app.MapPost("/api/admin/ban", _adminApi.HandleBan);
        app.MapPost("/api/admin/unban", _adminApi.HandleUnban);
        app.MapPost("/api/admin/resetPassword", _adminApi.HandleReset);
        app.MapPost("/api/admin/deleteUser", _adminApi.HandleDeleteUser);
        app.MapGet("/api/admin/relations", _adminApi.HandleRelations);
        app.MapGet("/api/admin/groups", _adminApi.HandleGroups);
        app.MapPost("/api/admin/unfriend", _adminApi.HandleUnfriend);
        app.MapPost("/api/admin/kickGroup", _adminApi.HandleKickGroup);
        app.MapPost("/api/admin/disbandGroup", _adminApi.HandleDisbandGroup);
        app.MapPost("/api/admin/renameGroup", _adminApi.HandleRenameGroup);
        app.MapPost("/api/admin/addFriend", _adminApi.HandleAddFriend);
        app.MapPost("/api/admin/addToGroup", _adminApi.HandleAddToGroup);
        app.MapPost("/api/admin/createGroup", _adminApi.HandleCreateGroup);

        // 路由 ChatHub 事件:消息 / 用户 / 好友 / 群变化推给相关在线连接
        _chatHub.MessageAdded += RouteMessage;
        _chatHub.UsersChanged += PushUsers;
        _chatHub.FriendRequestReceived += PushFriendRequestReceived;
        _chatHub.FriendRequestResult += PushFriendRequestResult;
        _chatHub.FriendRequestHandled += PushFriendRequestHandled;
        _chatHub.FriendAdded += PushFriendAdded;
        _chatHub.FriendDeleted += PushFriendDeleted;
        _chatHub.GroupUpdated += PushGroupUpdated;
        _chatHub.GroupRemoved += PushGroupRemoved;
        _chatHub.GroupRequestReceived += PushGroupRequestReceived;
        _chatHub.GroupRequestResult += PushGroupRequestResult;
        _chatHub.GroupRequestHandled += PushGroupRequestHandled;
        _chatHub.GroupInviteReceived += PushGroupInviteReceived;
        _chatHub.GroupInviteResult += PushGroupInviteResult;
        _chatHub.GroupInviteHandled += PushGroupInviteHandled;
        _chatHub.ProfileUpdated += PushProfileUpdated;
        _eventsSubscribed = true;

        _app = app;
        _ = RunAppAsync(app);
    }

    /// <summary>异步运行 Kestrel;启动失败(端口占用等)写入日志,避免静默无服务器。</summary>
    private static async Task RunAppAsync(WebApplication app)
    {
        try
        {
            await app.RunAsync();
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "server_error.log"),
                    $"{DateTime.Now:O} server start failed: {ex}\n");
            }
            catch { }
        }
    }

    public void Stop()
    {
        if (_eventsSubscribed)
        {
            _chatHub.MessageAdded -= RouteMessage;
            _chatHub.UsersChanged -= PushUsers;
            _chatHub.FriendRequestReceived -= PushFriendRequestReceived;
            _chatHub.FriendRequestResult -= PushFriendRequestResult;
            _chatHub.FriendRequestHandled -= PushFriendRequestHandled;
            _chatHub.FriendAdded -= PushFriendAdded;
            _chatHub.FriendDeleted -= PushFriendDeleted;
            _chatHub.GroupUpdated -= PushGroupUpdated;
            _chatHub.GroupRemoved -= PushGroupRemoved;
            _chatHub.GroupRequestReceived -= PushGroupRequestReceived;
            _chatHub.GroupRequestResult -= PushGroupRequestResult;
            _chatHub.GroupRequestHandled -= PushGroupRequestHandled;
            _chatHub.GroupInviteReceived -= PushGroupInviteReceived;
            _chatHub.GroupInviteResult -= PushGroupInviteResult;
            _chatHub.GroupInviteHandled -= PushGroupInviteHandled;
            _chatHub.ProfileUpdated -= PushProfileUpdated;
            _eventsSubscribed = false;
        }
        foreach (var ws in _clients.Keys)
        {
            try { ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "server stop", CancellationToken.None).GetAwaiter().GetResult(); }
            catch { }
        }
        _clients.Clear();
        _tokens.Clear();
        _wsTokens.Clear();
        _app?.StopAsync();
        _app = null;
    }

    /// <summary>踢掉某用户的全部在线连接(管理端禁用/删除账号时调用)。
    /// 立即摘路由/令牌(旧 token 即刻失效),发 error 帧,再延迟 Abort:
    /// 立即 Abort 的 RST 会清空未读接收缓冲,客户端收不到禁用提示;500ms 让 error 先到达。
    /// Abort 不等待 close 握手,避免线程挂死。</summary>
    public void KickUser(string userId, string reason)
    {
        foreach (var (ws, uid) in _clients)
        {
            if (uid == userId)
            {
                if (_wsTokens.TryRemove(ws, out var token))
                    _tokens.TryRemove(token, out _);
                _clients.TryRemove(ws, out _);
                SendJson(ws, new { t = Protocol.Error, reason });
                var target = ws;
                _ = Task.Delay(500).ContinueWith(_ =>
                {
                    try { target.Abort(); } catch { }
                });
            }
        }
    }

    // ---- WebSocket 连接 ----

    private async Task HandleWebSocket(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var userId = "";
        var buffer = new byte[16 * 1024];

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                WsFrame? frame;
                try
                {
                    frame = JsonSerializer.Deserialize<WsFrame>(
                        Encoding.UTF8.GetString(buffer, 0, result.Count), JsonOpts.Web);
                }
                catch (JsonException)
                {
                    continue; // 坏帧(如 null 字段类型不匹配):跳过,不杀整个连接
                }
                if (frame is null)
                    continue;

                switch (frame.T)
                {
                    // ---- 认证(连接首帧;失败只回 error,不关连接,客户端可重试) ----
                    case Protocol.Register:
                        userId = DoRegister(ws, frame);
                        break;

                    case Protocol.Auth:
                        userId = DoAuth(ws, frame);
                        break;

                    // ---- 业务帧:必须先认证 ----
                    default:
                        if (userId.Length == 0)
                        {
                            // 黑名单式:未认证只放行上面的 register/auth,其余帧一律拒绝。
                            // (反白名单:新增业务帧无需登记放行,不会漏)
                            SendJson(ws, new { t = Protocol.Error, reason = "请先登录" });
                            break;
                        }
                        Dispatch(ws, userId, frame);
                        break;
                }
            }
        }
        catch (WebSocketException ex)
        {
            // 连接异常断开:在线状态交给 TTL 自然清理,不立即移除(网络抖动重连场景)
            Console.WriteLine($"{DateTime.Now:O} RECV-EX state={ws.State} {ex.Message}");
        }
        catch (Exception ex)
        {
            // 处理帧时出错:记录日志,连接交给 using 释放
            Console.WriteLine($"{DateTime.Now:O} [{userId}] {ex}");
        }
        finally
        {
            _clients.TryRemove(ws, out _);
            _sendLocks.TryRemove(ws, out _);
            if (userId.Length > 0)
                OnConnectionGone(ws, userId);
        }
    }

    /// <summary>一条连接结束:回收其令牌,仅当该用户再无其他在线连接才判离线(多设备各自独立)。</summary>
    private void OnConnectionGone(WebSocket ws, string userId)
    {
        if (_wsTokens.TryRemove(ws, out var token))
            _tokens.TryRemove(token, out _);
        if (!_clients.Values.Contains(userId))
            _chatHub.RemoveUser(userId);
    }

    /// <summary>注册账号,成功回 authOk + welcome 并上线。返回认证后的 UserId(失败为空)。</summary>
    private string DoRegister(WebSocket ws, WsFrame frame)
    {
        try
        {
            var user = _chatHub.Register(frame.Account ?? "", frame.Password ?? "", frame.Nickname ?? "");
            return AuthSucceeded(ws, user);
        }
        catch (ArgumentException ex)
        {
            SendJson(ws, new { t = Protocol.Error, reason = ex.Message });
            return "";
        }
    }

    /// <summary>登录,成功回 authOk + welcome 并上线。返回认证后的 UserId(失败为空)。</summary>
    private string DoAuth(WebSocket ws, WsFrame frame)
    {
        try
        {
            var user = _chatHub.Authenticate(frame.Account ?? "", frame.Password ?? "");
            return AuthSucceeded(ws, user);
        }
        catch (ArgumentException ex)
        {
            SendJson(ws, new { t = Protocol.Error, reason = ex.Message });
            return "";
        }
    }

    /// <summary>认证成功公共路径:登记连接 + 上线 + 回 authOk(含会话令牌)+ welcome 全量快照 + 推在线列表。</summary>
    private string AuthSucceeded(WebSocket ws, UserInfo user)
    {
        // 同一连接重复认证(如已登录又发 register):先摘掉旧身份,避免旧身份幽灵在线
        if (_clients.TryRemove(ws, out var oldId) && oldId.Length > 0 && oldId != user.UserId)
            OnConnectionGone(ws, oldId);

        _clients[ws] = user.UserId;
        _chatHub.TouchUser(user.UserId, user.Nickname);

        // 下发一次性会话令牌:HTTP 兜底接口(/api/messages)用它换真实身份
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _tokens[token] = user.UserId;
        _wsTokens[ws] = token;

        SendJson(ws, new
        {
            t = Protocol.AuthOk,
            userId = user.UserId,
            nickname = user.Nickname,
            petSize = _chatHub.GetPetSize(user.UserId),
            token,
        });
        SendJson(ws, new
        {
            t = Protocol.Welcome,
            seq = _chatHub.GetLastSeq(),
            friends = _chatHub.GetFriends(user.UserId),
            groups = _chatHub.GetGroups(user.UserId),
            requests = _chatHub.GetPendingRequests(user.UserId),
            groupRequests = _chatHub.GetGroupRequests(user.UserId),
            groupInvites = _chatHub.GetPendingGroupInvites(user.UserId),
            readStates = _chatHub.GetReadStates(user.UserId),
            settings = _chatHub.GetUserSettings(user.UserId),
            unreadCounts = _chatHub.GetUnreadCounts(user.UserId),
        });
        PushUsers();
        return user.UserId;
    }

    /// <summary>分发已认证的业务帧。</summary>
    private void Dispatch(WebSocket ws, string userId, WsFrame frame)
    {
        switch (frame.T)
        {
            case Protocol.Chat:
                // 入库 + 事件触发;路由推送由 RouteMessage 统一做
                RunOrError(ws, () => _chatHub.Send(userId, _chatHub.GetNickname(userId), frame.ConvType, frame.ConvId ?? "", frame.Text ?? ""));
                break;

            case Protocol.Read:
                // 已读上报:游标推进才推回执给会话其他人(私聊 = 对方,群聊 = 全体成员除自己)
                if (_chatHub.MarkRead(userId, frame.ConvType, frame.ConvId ?? "", frame.Seq))
                {
                    // 打开会话 = 该会话未读归零(服务器权威),回推本人全部连接
                    // 会话键 = 接收者视角:私聊 frame.ConvId 即对方,群聊即群 ID
                    var curKey = ChatHub.ReceiverConvKey(frame.ConvType, frame.ConvId ?? "", frame.ConvId ?? "");
                    _chatHub.ClearUnread(userId, curKey);
                    foreach (var (sock, uid) in _clients)
                    {
                        if (uid == userId)
                            SendJson(sock, new { t = Protocol.UnreadUpdate, convKey = curKey, count = 0 });
                    }
                    var peers = frame.ConvType == 1
                        ? _chatHub.GetGroupMemberIds(frame.ConvId ?? "")
                        : new List<string> { frame.ConvId ?? "" };
                    foreach (var (peerWs, uid) in _clients)
                    {
                        if (uid == userId || !peers.Contains(uid))
                            continue;
                        // 接收者视角:私聊的 convId = 读的人;群聊 = 群 ID
                        SendJson(peerWs, new
                        {
                            t = Protocol.Read,
                            convType = frame.ConvType,
                            convId = frame.ConvType == 0 ? userId : frame.ConvId,
                            fromUserId = userId,
                            seq = frame.Seq,
                        });
                    }
                }
                break;

            case Protocol.FriendSearch:
                SendJson(ws, new
                {
                    t = Protocol.FriendSearchResult,
                    users = _chatHub.SearchUsers(frame.Keyword ?? "", userId),
                });
                break;

            case Protocol.FriendRequest:
                // 成功由 FriendRequestReceived 事件推给被申请人;这里不回执,发送者本地刷新
                RunOrError(ws, () => _chatHub.SendFriendRequest(userId, frame.TargetUserId ?? "", frame.Text ?? ""));
                break;

            case Protocol.UserProfile:
                // 群聊里点头像查看资料:昵称/账号 + 好友状态(加好友入口用)
                var profile = _chatHub.GetUserProfile(userId, frame.TargetUserId ?? "");
                if (profile is null)
                    SendJson(ws, new { t = Protocol.Error, reason = "用户不存在" });
                else
                    SendJson(ws, new { t = Protocol.UserProfileResult, profile });
                break;

            case Protocol.FriendRespond:
                // 成功由 FriendRequestResult / FriendAdded 事件推送;失败(不是你的申请等)回 error
                if (!_chatHub.RespondRequest(userId, frame.RequestId, frame.Accept))
                    SendJson(ws, new { t = Protocol.Error, reason = "该申请不存在或已处理" });
                break;

            case Protocol.FriendRemark:
                if (!_chatHub.SetFriendRemark(userId, frame.TargetUserId ?? "", frame.Remark ?? ""))
                    SendJson(ws, new { t = Protocol.Error, reason = "备注需为 1-20 字" });
                break;

            case Protocol.SetSetting:
                // 云端键值设置:落库 + 推给本人所有连接(多端实时一致);其他账号不受影响
                _chatHub.SetUserSetting(userId, frame.Key ?? "", frame.Value ?? "");
                foreach (var (sock, uid) in _clients)
                {
                    if (uid == userId)
                        SendJson(sock, new { t = Protocol.SettingUpdated, key = frame.Key ?? "", value = frame.Value ?? "" });
                }
                break;

            case Protocol.FriendDelete:
                // 成功由 FriendDeleted 事件推给双方;失败(不是好友)回 error
                if (!_chatHub.DeleteFriend(userId, frame.TargetUserId ?? ""))
                    SendJson(ws, new { t = Protocol.Error, reason = "还不是好友" });
                break;

            case Protocol.GroupCreate:
                // 成功由 GroupUpdated 事件推给全体成员(含创建者)
                RunOrError(ws, () => _chatHub.CreateGroup(frame.Name ?? "", frame.MemberIds ?? new List<string>(), userId));
                break;

            case Protocol.GroupRename:
                if (!_chatHub.RenameGroup(frame.GroupId ?? "", frame.Name ?? "", userId))
                    SendJson(ws, new { t = Protocol.Error, reason = "群不存在或你不是成员" });
                break;

            case Protocol.GroupSearch:
                // 按群名或群号模糊搜索(全库);结果列表,客户端显示候选
                SendJson(ws, new
                {
                    t = Protocol.GroupSearchResult,
                    groups = _chatHub.SearchGroups(frame.Keyword ?? "", userId),
                });
                break;

            case Protocol.GroupJoin:
                // 成功由 GroupUpdated 事件推给全体成员(含新加入者)
                RunOrError(ws, () => _chatHub.JoinGroup(frame.GroupId ?? "", userId));
                break;

            case Protocol.GroupLeave:
                // 普通成员退出 → GroupUpdated;群主退出 = 解散 → GroupRemoved 推给原全体成员
                RunOrError(ws, () => _chatHub.LeaveGroup(frame.GroupId ?? "", userId));
                break;

            case Protocol.GroupRequestSend:
                // 发加群申请;成功由 GroupRequestReceived 推给群主/管理员,申请人在本地刷新
                RunOrError(ws, () => _chatHub.SendGroupRequest(frame.GroupId ?? "", userId));
                break;

            case Protocol.GroupRequestRespond:
                // 审批加群申请;同意后由 GroupUpdated 推成员列表,申请人收到 GroupRequestResult
                RunOrError(ws, () =>
                {
                    if (!_chatHub.RespondGroupRequest(userId, frame.RequestId, frame.Accept))
                        SendJson(ws, new { t = Protocol.Error, reason = "申请不存在或已处理" });
                });
                break;

            case Protocol.GroupAdminSet:
                // 群主设置/取消管理员;成功由 GroupUpdated 广播新成员列表(含 AdminIds)
                RunOrError(ws, () => _chatHub.SetGroupAdmin(frame.GroupId ?? "", frame.TargetUserId ?? "", frame.Accept, userId));
                break;

            case Protocol.GroupRemoveMember:
                // 群主移出成员;剩余成员收 groupUpdated,被移者收 groupRemoved(带原因)
                RunOrError(ws, () => _chatHub.RemoveGroupMember(frame.GroupId ?? "", frame.TargetUserId ?? "", userId));
                break;

            case Protocol.GroupInviteSend:
                // 发群邀请(拉好友进群);成功由 GroupInviteReceived 推给被邀者,发起者本地刷新
                RunOrError(ws, () => _chatHub.SendGroupInvite(frame.GroupId ?? "", frame.TargetUserId ?? "", userId));
                break;

            case Protocol.GroupInviteRespond:
                // 审批群邀请;同意后由 GroupUpdated 推成员列表,发起者收到 GroupInviteResult
                RunOrError(ws, () => _chatHub.RespondGroupInvite(userId, frame.RequestId, frame.Accept));
                break;

            case Protocol.ProfileUpdate:
                if (!_chatHub.UpdateNickname(userId, frame.Nickname ?? ""))
                    SendJson(ws, new { t = Protocol.Error, reason = "昵称需为 1-20 字" });
                break;

            case Protocol.PetSizeUpdate:
                // 缩放尺寸云端同步:写库后推给该账号全部在线连接(同账号多端一起刷新)
                if (frame.PetSize < PetConfig.MinPetSize || frame.PetSize > PetConfig.MaxPetSize)
                    SendJson(ws, new { t = Protocol.Error, reason = "宠物尺寸超出范围" });
                else
                {
                    _chatHub.UpdatePetSize(userId, frame.PetSize);
                    PushPetSize(userId, frame.PetSize);
                }
                break;

            case Protocol.Logout:
                // 主动退出登录:先摘连接再判离线(否则 Contains 检查时自己还在列表里,
                // 永不离线),然后发 Close 帧(CloseOutputAsync 不等客户端回握手,不阻塞)。
                _clients.TryRemove(ws, out _);
                OnConnectionGone(ws, userId);
                try
                {
                    _ = ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "logout", CancellationToken.None);
                }
                catch { }
                break;

            case Protocol.Ping:
                _chatHub.TouchUser(userId, _chatHub.GetNickname(userId));
                SendJson(ws, new { t = Protocol.Pong });
                break;
        }
    }

    /// <summary>执行业务操作;业务校验失败(ArgumentException)转 error 帧回给发起连接,其余异常由外层兜底。</summary>
    private void RunOrError(WebSocket ws, Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException ex)
        {
            SendJson(ws, new { t = Protocol.Error, reason = ex.Message });
        }
    }

    // ---- ChatHub 事件 → 帧推送 ----

    /// <summary>向满足条件的在线连接推送同一帧(遍历/条件语义与原 Push* 模板一致)。</summary>
    private void SendToUsers(Func<string, bool> match, object payload)
    {
        foreach (var (ws, uid) in _clients)
        {
            if (match(uid))
                SendJson(ws, payload);
        }
    }

    /// <summary>路由一条新消息:私聊双方 / 群聊全体成员(发送者也在其中,顺带回显)。
    /// 私聊 convId 按接收者视角转换(历史补拉在 GetMessages 已转换,实时推送此前漏了,
    /// 接收者会拿到自己的 UserId 当会话 ID,未读/已读全部错乱)。</summary>
    private void RouteMessage(ChatMessage msg)
    {
        var recipients = _chatHub.GetMessageRecipients(msg).ToHashSet();
        foreach (var (ws, uid) in _clients)
        {
            if (recipients.Contains(uid))
            {
                SendJson(ws, ToChatFrame(msg, uid));
                // 未读红点同步:接收者 +1 后的新计数(发送者不加,其连接只收消息回显)
                if (uid != msg.FromUserId)
                {
                    var convKey = ChatHub.ReceiverConvKey(msg.ConvType, msg.ConvId, msg.FromUserId);
                    SendJson(ws, new
                    {
                        t = Protocol.UnreadUpdate,
                        convKey,
                        count = _chatHub.GetUnreadCount(uid, convKey),
                    });
                }
            }
        }
    }

    private static object ToChatFrame(ChatMessage msg, string viewerId) => new
    {
        t = Protocol.Chat,
        msg.Seq,
        convType = msg.ConvType,
        // 私聊且看者不是发送者:会话 ID 换成发送者(看者视角的"对方")
        convId = msg.ConvType == 0 && msg.FromUserId != viewerId ? msg.FromUserId : msg.ConvId,
        fromUserId = msg.FromUserId,
        fromName = msg.FromName,
        msg.Text,
        time = msg.Time.ToString("O"),
        type = msg.Type,
    };

    /// <summary>推在线用户列表给所有连接。</summary>
    private void PushUsers()
    {
        var users = _chatHub.GetUsers();
        SendToUsers(_ => true, new { t = Protocol.Users, users });
    }

    /// <summary>新好友申请 → 推给被申请人。</summary>
    private void PushFriendRequestReceived(FriendRequest req)
    {
        SendToUsers(uid => uid == req.ToUserId, new { t = Protocol.FriendRequestReceived, request = req });
    }

    /// <summary>申请处理结果 → 推给申请人。</summary>
    private void PushFriendRequestResult(FriendRequest req, bool accepted)
    {
        SendToUsers(uid => uid == req.FromUserId, new
        {
            t = Protocol.FriendRequestResult,
            requestId = req.ReqId,
            accepted,
            fromUserId = req.ToUserId,
            fromName = _chatHub.GetNickname(req.ToUserId),
        });
    }

    /// <summary>成为好友 → 推给双方刷新列表(各自视角的对方 = friend)。</summary>
    private void PushFriendAdded(FriendRequest req)
    {
        // 双方各自视角的"对方"不同,拆两次推送(条件互斥,与原 if/else 等价)
        SendToUsers(uid => uid == req.FromUserId,
            new { t = Protocol.FriendAdded, friend = new { userId = req.ToUserId, nickname = _chatHub.GetNickname(req.ToUserId) } });
        SendToUsers(uid => uid == req.ToUserId,
            new { t = Protocol.FriendAdded, friend = new { userId = req.FromUserId, nickname = req.FromName } });
    }

    /// <summary>申请被处理 → 推给被申请人(参数 = 接收方 UserId, 申请 ID)。</summary>
    private void PushFriendRequestHandled(string toUserId, long requestId)
    {
        SendToUsers(uid => uid == toUserId, new { t = Protocol.FriendRequestHandled, requestId });
    }

    /// <summary>删除好友 → 推给接收方(参数 = 接收方 UserId, 被删好友 UserId)。</summary>
    private void PushFriendDeleted(string toUserId, string deletedFriendId)
    {
        SendToUsers(uid => uid == toUserId, new { t = Protocol.FriendDeleted, userId = deletedFriendId });
    }

    /// <summary>建群 / 群改名 / 成员变动 → 推给全体成员(群信息含成员列表)。</summary>
    private void PushGroupUpdated(GroupInfo group)
    {
        var memberIds = group.Members.Select(m => m.UserId).ToHashSet();
        SendToUsers(uid => memberIds.Contains(uid), new { t = Protocol.GroupUpdated, group });
    }

    /// <summary>群解散(群主退出)/被移出 → 推给当事人:参数 = (成员 UserId, 群 ID, 原因;解散为空)。</summary>
    private void PushGroupRemoved(string memberUserId, string groupId, string reason)
    {
        SendToUsers(uid => uid == memberUserId, new { t = Protocol.GroupRemoved, groupId, reason });
    }

    /// <summary>新加群申请 → 推给群主/管理员:参数 = (接收方 UserId, 申请)。</summary>
    private void PushGroupRequestReceived(string toUserId, GroupRequest request)
    {
        SendToUsers(uid => uid == toUserId, new { t = Protocol.GroupRequestReceived, request });
    }

    /// <summary>加群申请有结果 → 推给申请人:参数 = (申请, 是否同意)。</summary>
    private void PushGroupRequestResult(GroupRequest request, bool accepted)
    {
        SendToUsers(uid => uid == request.FromUserId, new { t = Protocol.GroupRequestResult, request, accepted });
    }

    /// <summary>加群申请被处理 → 推给群主/管理员刷新列表:参数 = (接收方 UserId, 申请 ID)。</summary>
    private void PushGroupRequestHandled(string toUserId, long requestId)
    {
        SendToUsers(uid => uid == toUserId, new { t = Protocol.GroupRequestHandled, requestId });
    }

    /// <summary>新群邀请(被拉进群) → 推给被邀者:参数 = (接收方 UserId, 邀请)。</summary>
    private void PushGroupInviteReceived(string toUserId, GroupInvite invite)
    {
        SendToUsers(uid => uid == toUserId, new { t = Protocol.GroupInviteReceived, invite });
    }

    /// <summary>群邀请有结果 → 推给发起者:参数 = (邀请, 是否同意)。</summary>
    private void PushGroupInviteResult(GroupInvite invite, bool accepted)
    {
        SendToUsers(uid => uid == invite.FromUserId, new
        {
            t = Protocol.GroupInviteResult,
            inviteId = invite.InviteId,
            accepted,
            groupId = invite.GroupId,
            groupName = invite.GroupName,
        });
    }

    /// <summary>群邀请被处理 → 推给被邀者刷新列表:参数 = (接收方 UserId, 邀请 ID)。</summary>
    private void PushGroupInviteHandled(string toUserId, long inviteId)
    {
        SendToUsers(uid => uid == toUserId, new { t = Protocol.GroupInviteHandled, inviteId });
    }

    /// <summary>好友昵称变更 → 推给其好友。</summary>
    private void PushProfileUpdated(string userId, string nickname)
    {
        SendToUsers(uid => uid != userId && _chatHub.IsFriend(uid, userId),
            new { t = Protocol.ProfileUpdated, userId, nickname });
    }

    /// <summary>宠物尺寸变更 → 推给该账号全部在线连接(自己的其他端实时刷新)。</summary>
    private void PushPetSize(string userId, double petSize)
    {
        SendToUsers(uid => uid == userId, new { t = Protocol.PetSizeUpdated, petSize });
    }

    // ---- 发送 ----

    /// <summary>串行发送 JSON 文本帧(WebSocket 不能并发 Send)。</summary>
    private void SendJson(WebSocket ws, object payload)
    {
        if (ws.State != WebSocketState.Open)
            return;

        var json = JsonSerializer.Serialize(payload, JsonOpts.Web);
        var bytes = Encoding.UTF8.GetBytes(json);
        var gate = _sendLocks.GetOrAdd(ws, _ => new object());
        lock (gate)
        {
            try
            {
                ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (WebSocketException ex)
            {
                // 对端已断开:接收循环会处理清理
                Console.WriteLine($"{DateTime.Now:O} SEND-EX state={ws.State} {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 获取本机正在使用的局域网 IPv4 地址。
    /// 用 UDP 假连接探测系统实际会走哪个网卡,避免虚拟机/VPN 网卡干扰。
    /// </summary>
    private static string GetLocalIp()
    {
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // 不实际发包,只是让系统决定路由;8.8.8.8 是公网 DNS,不会连上
            sock.Connect("8.8.8.8", 65530);
            var local = sock.LocalEndPoint as IPEndPoint;
            return local?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}
