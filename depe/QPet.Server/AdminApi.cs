using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using QPet.Core;

namespace QPet.Server;

/// <summary>
/// 管理 REST(/api/admin/*):管理密码登录 → 一次性 token → 账户列表/禁用/解禁/重置密码。
/// 局域网内使用,无 HTTPS;token 只存内存,服务器重启即失效(管理端重新登录)。
/// </summary>
public sealed class AdminApi
{
    private readonly ChatHub _hub;
    private readonly Action<string, string> _kickUser; // userId, reason(禁用/删除文案区分)
    private readonly string _password;
    private readonly Func<string>? _getEndpoint; // 登录响应附带 ws 地址(管理窗口显示二维码用)
    private string _token = ""; // 登录成功后签发(内存,不持久化)

    public AdminApi(ChatHub hub, string password, Action<string, string> kickUser, Func<string>? getEndpoint = null)
    {
        _hub = hub;
        _password = password;
        _kickUser = kickUser;
        _getEndpoint = getEndpoint;
    }

    /// <summary>POST /api/admin/login  body: {"password":"..."} → {"token":"...","endpoint":"..."} 或 401。</summary>
    public async Task HandleLogin(HttpContext ctx)
    {
        var body = await ReadBodyAsync(ctx, "password");
        if (body["password"] != _password)
        {
            await WriteError(ctx, StatusCodes.Status401Unauthorized, "管理密码错误");
            return;
        }
        // 每次登录签发新 token(旧 token 立即失效)
        _token = Guid.NewGuid().ToString("N");
        await ctx.Response.WriteAsJsonAsync(new { token = _token, endpoint = _getEndpoint?.Invoke() ?? "" });
    }

    /// <summary>GET /api/admin/users  → {"users":[AdminUser...]}。</summary>
    public async Task HandleUsers(HttpContext ctx)
    {
        if (await RequireAuthorized(ctx))
            return;
        await ctx.Response.WriteAsJsonAsync(new { users = _hub.GetAllUsers() });
    }

    /// <summary>POST /api/admin/createUser  body: {"account":"...","password":"...","nickname":"..."} → 创建账户(复用注册校验)。</summary>
    public async Task HandleCreateUser(HttpContext ctx)
    {
        if (await RequireAuthorized(ctx))
            return;

        var body = await ReadBodyAsync(ctx, "account", "password", "nickname");
        if (!await RequireParams(ctx, body, "account", "password", "nickname"))
            return;

        try
        {
            _hub.Register(body["account"]!, body["password"]!, body["nickname"]!);
        }
        catch (ArgumentException ex)
        {
            await WriteError(ctx, StatusCodes.Status400BadRequest, ex.Message);
            return;
        }
        await ctx.Response.WriteAsJsonAsync(new { ok = true });
    }

    /// <summary>POST /api/admin/ban  body: {"userId":"..."} → 禁用(踢下线)。</summary>
    public async Task HandleBan(HttpContext ctx)
    {
        await DoSetBanned(ctx, true);
    }

    /// <summary>POST /api/admin/unban  body: {"userId":"..."} → 解禁。</summary>
    public async Task HandleUnban(HttpContext ctx)
    {
        await DoSetBanned(ctx, false);
    }

    private async Task DoSetBanned(HttpContext ctx, bool banned)
    {
        if (await RequireAuthorized(ctx))
            return;
        var userId = await ReadUserId(ctx);
        if (userId is null)
            return;

        if (!_hub.SetUserBanned(userId, banned))
        {
            await WriteError(ctx, StatusCodes.Status404NotFound, "用户不存在");
            return;
        }
        if (banned)
            _kickUser(userId, "账号已被管理员禁用"); // 踢掉已连的 WebSocket
        await ctx.Response.WriteAsJsonAsync(new { ok = true });
    }

    /// <summary>POST /api/admin/resetPassword  body: {"userId":"...","newPassword":"..."}。</summary>
    public async Task HandleReset(HttpContext ctx)
    {
        if (await RequireAuthorized(ctx))
            return;

        var body = await ReadBodyAsync(ctx, "userId", "newPassword");
        if (!await RequireParams(ctx, body, "userId", "newPassword"))
            return;

        try
        {
            if (!_hub.ResetPassword(body["userId"]!, body["newPassword"]!))
            {
                await WriteError(ctx, StatusCodes.Status404NotFound, "用户不存在");
                return;
            }
        }
        catch (ArgumentException ex)
        {
            await WriteError(ctx, StatusCodes.Status400BadRequest, ex.Message);
            return;
        }
        await ctx.Response.WriteAsJsonAsync(new { ok = true });
    }

    /// <summary>POST /api/admin/deleteUser  body: {"userId":"..."} → 彻底删除账户(好友/群/消息连带删)。</summary>
    public async Task HandleDeleteUser(HttpContext ctx)
    {
        if (await RequireAuthorized(ctx))
            return;
        var userId = await ReadUserId(ctx);
        if (userId is null)
            return;

        // 特殊管理员账号不可删除(防绕过管理端 UI 直接调 API)
        if (_hub.GetAllUsers().Any(u => u.UserId == userId && u.Account == AppConstants.AdminAccount))
        {
            await WriteError(ctx, StatusCodes.Status400BadRequest, "管理员账号不可删除");
            return;
        }

        if (!_hub.DeleteUser(userId))
        {
            await WriteError(ctx, StatusCodes.Status404NotFound, "用户不存在");
            return;
        }
        _kickUser(userId, "账号已被管理员删除"); // 踢掉已连的 WebSocket
        await ctx.Response.WriteAsJsonAsync(new { ok = true });
    }

    /// <summary>GET /api/admin/relations?userId=xxx → 该用户的好友列表 + 群聊列表(管理关系用)。</summary>
    public async Task HandleRelations(HttpContext ctx)
    {
        if (await RequireAuthorized(ctx))
            return;
        var userId = ctx.Request.Query["userId"].ToString();
        if (string.IsNullOrEmpty(userId))
        {
            await WriteError(ctx, StatusCodes.Status400BadRequest, "缺少 userId");
            return;
        }

        var friends = _hub.GetFriends(userId)
            .Select(f => new { f.UserId, f.Account, f.Nickname, Online = _hub.IsOnline(f.UserId) });
        var groups = _hub.GetGroups(userId)
            .Select(g => new
            {
                g.GroupId,
                g.Num,
                g.Name,
                g.IsPublic,
                Members = g.Members.Select(m => new { m.UserId, m.Nickname }),
            });
        await ctx.Response.WriteAsJsonAsync(new { friends, groups });
    }

    /// <summary>GET /api/admin/groups → 全部群列表(管理端加群候选,含成员数)。</summary>
    public async Task HandleGroups(HttpContext ctx)
    {
        if (await RequireAuthorized(ctx))
            return;
        var groups = _hub.GetAllGroups()
            .Select(g => new
            {
                g.GroupId,
                g.Num,
                g.Name,
                g.IsPublic,
                g.OwnerId,
                g.AdminIds,
                MemberCount = g.Members.Count,
                Members = g.Members.Select(m => new
                {
                    m.UserId,
                    m.Nickname,
                    m.Account,
                    IsAdmin = g.AdminIds.Contains(m.UserId),
                }),
            });
        await ctx.Response.WriteAsJsonAsync(new { groups });
    }

    /// <summary>POST /api/admin/unfriend  body: {"userId":"...","friendUserId":"..."} → 解除双方好友关系(在线客户端即时刷新)。</summary>
    public async Task HandleUnfriend(HttpContext ctx)
    {
        if (await RequireAuthorized(ctx))
            return;
        var body = await ReadBodyAsync(ctx, "userId", "friendUserId");
        if (!await RequireParams(ctx, body, "userId", "friendUserId"))
            return;

        if (!_hub.DeleteFriend(body["userId"]!, body["friendUserId"]!))
        {
            await WriteError(ctx, StatusCodes.Status400BadRequest, "还不是好友");
            return;
        }
        await ctx.Response.WriteAsJsonAsync(new { ok = true });
    }

    /// <summary>POST /api/admin/addFriend  body: {"userId":"...","targetAccount":"..."} → 直接加好友(跳过申请审批)。</summary>
    public async Task HandleAddFriend(HttpContext ctx)
    {
        if (await RequireAuthorized(ctx))
            return;
        var body = await ReadBodyAsync(ctx, "userId", "targetAccount");
        if (!await RequireParams(ctx, body, "userId", "targetAccount"))
            return;

        try
        {
            if (!_hub.DirectAddFriend(body["userId"]!, body["targetAccount"]!))
            {
                await WriteError(ctx, StatusCodes.Status400BadRequest, "已经是好友");
                return;
            }
        }
        catch (ArgumentException ex)
        {
            await WriteError(ctx, StatusCodes.Status400BadRequest, ex.Message);
            return;
        }
        await ctx.Response.WriteAsJsonAsync(new { ok = true });
    }

    /// <summary>POST /api/admin/disbandGroup  body: {"groupId":"..."} → 解散群聊(公共群不可解散,全部成员在线即时移除)。</summary>
    public async Task HandleDisbandGroup(HttpContext ctx)
    {
        if (await RequireAuthorized(ctx))
            return;
        var body = await ReadBodyAsync(ctx, "groupId");
        if (!await RequireParams(ctx, body, "groupId"))
            return;

        if (!_hub.DisbandGroupByAdmin(body["groupId"]!))
        {
            await WriteError(ctx, StatusCodes.Status400BadRequest, "公共群聊不能解散");
            return;
        }
        await ctx.Response.WriteAsJsonAsync(new { ok = true });
    }

    /// <summary>POST /api/admin/renameGroup  body: {"groupId":"...","name":"..."} → 改群名(管理员权限;公共群不可改)。</summary>
    public async Task HandleRenameGroup(HttpContext ctx)
    {
        if (await RequireAuthorized(ctx))
            return;
        var body = await ReadBodyAsync(ctx, "groupId", "name");
        if (!await RequireParams(ctx, body, "groupId", "name"))
            return;

        if (!_hub.RenameGroupByAdmin(body["groupId"]!, body["name"]!))
        {
            await WriteError(ctx, StatusCodes.Status400BadRequest, "公共群不可改名或群名需为 1-20 字");
            return;
        }
        await ctx.Response.WriteAsJsonAsync(new { ok = true });
    }

    /// <summary>POST /api/admin/kickGroup  body: {"userId":"...","groupId":"..."} → 把用户移出群聊
    /// (剩余成员即时刷新;被踢者是群主时群主自动顺位转让,群不会变无主)。</summary>
    public async Task HandleKickGroup(HttpContext ctx)
    {
        if (await RequireAuthorized(ctx))
            return;
        var body = await ReadBodyAsync(ctx, "userId", "groupId");
        if (!await RequireParams(ctx, body, "userId", "groupId"))
            return;

        if (!_hub.KickFromGroup(body["groupId"]!, body["userId"]!))
        {
            await WriteError(ctx, StatusCodes.Status400BadRequest, "该用户不在这个群");
            return;
        }
        await ctx.Response.WriteAsJsonAsync(new { ok = true });
    }

    /// <summary>POST /api/admin/addToGroup  body: {"userId":"...","groupNum":"..."} → 直接加群(跳过申请审批)。</summary>
    public async Task HandleAddToGroup(HttpContext ctx)
    {
        if (await RequireAuthorized(ctx))
            return;
        var body = await ReadBodyAsync(ctx, "userId", "groupNum");
        if (!await RequireParams(ctx, body, "userId", "groupNum"))
            return;

        try
        {
            if (!_hub.DirectJoinGroup(body["userId"]!, body["groupNum"]!))
            {
                await WriteError(ctx, StatusCodes.Status400BadRequest, "该用户已在这个群");
                return;
            }
        }
        catch (ArgumentException ex)
        {
            await WriteError(ctx, StatusCodes.Status400BadRequest, ex.Message);
            return;
        }
        await ctx.Response.WriteAsJsonAsync(new { ok = true });
    }

    /// <summary>POST /api/admin/createGroup  body: {"ownerAccount":"...","name":"...","memberAccounts":"..."} → 以该账号为群主创建群(成员按账号解析,逗号分隔,可空;无需好友关系)。</summary>
    public async Task HandleCreateGroup(HttpContext ctx)
    {
        if (await RequireAuthorized(ctx))
            return;
        var body = await ReadBodyAsync(ctx, "ownerAccount", "name", "memberAccounts");
        if (!await RequireParams(ctx, body, "ownerAccount", "name"))
            return;

        // 账号 → UserId 映射(成员无需好友关系,管理端绕过好友校验;无效账号列出提示)
        var users = _hub.GetAllUsers().ToDictionary(u => u.Account, u => u.UserId);
        if (!users.TryGetValue(body["ownerAccount"]!.Trim(), out var ownerId))
        {
            await WriteError(ctx, StatusCodes.Status400BadRequest, "群主账号不存在");
            return;
        }
        var memberIds = new List<string>();
        var invalid = new List<string>();
        foreach (var a in (body["memberAccounts"] ?? "").Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (users.TryGetValue(a, out var uid))
                memberIds.Add(uid);
            else
                invalid.Add(a);
        }
        if (invalid.Count > 0)
        {
            await WriteError(ctx, StatusCodes.Status400BadRequest, $"成员账号不存在: {string.Join("、", invalid)}");
            return;
        }

        try
        {
            _hub.CreateGroupByAdmin(body["name"]!, ownerId, memberIds);
        }
        catch (ArgumentException ex)
        {
            await WriteError(ctx, StatusCodes.Status400BadRequest, ex.Message);
            return;
        }
        await ctx.Response.WriteAsJsonAsync(new { ok = true });
    }

    // ---- 内部 ----

    private bool Authorized(HttpContext ctx) =>
        _token.Length > 0 &&
        ctx.Request.Headers.Authorization.ToString() == $"Bearer {_token}";

    /// <summary>未登录则写 401 {"error":"未登录管理端"} 并返回 true(调用方随即 return)。</summary>
    private async Task<bool> RequireAuthorized(HttpContext ctx)
    {
        if (Authorized(ctx))
            return false;
        await WriteError(ctx, StatusCodes.Status401Unauthorized, "未登录管理端");
        return true;
    }

    /// <summary>解析请求体,取出命名字段(缺的键值为 null,键始终存在)。</summary>
    private static async Task<Dictionary<string, string?>> ReadBodyAsync(HttpContext ctx, params string[] names)
    {
        var values = names.ToDictionary(n => n, _ => (string?)null);
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        foreach (var name in names)
        {
            if (doc.RootElement.TryGetProperty(name, out var v))
                values[name] = v.GetString();
        }
        return values;
    }

    /// <summary>必填参数任一为空 → 400 {"error":"参数不完整"} 并返回 false。</summary>
    private static async Task<bool> RequireParams(HttpContext ctx, Dictionary<string, string?> body, params string[] required)
    {
        if (required.All(n => !string.IsNullOrEmpty(body[n])))
            return true;
        await WriteError(ctx, StatusCodes.Status400BadRequest, "参数不完整");
        return false;
    }

    /// <summary>写错误响应 {"error":message}(文案/状态码与各调用点原值一致)。</summary>
    private static async Task WriteError(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { error = message });
    }

    /// <summary>解析请求体取 userId,缺失 → 400 {"error":"缺少 userId"} 并返回 null。</summary>
    private async Task<string?> ReadUserId(HttpContext ctx)
    {
        var body = await ReadBodyAsync(ctx, "userId");
        if (string.IsNullOrEmpty(body["userId"]))
        {
            await WriteError(ctx, StatusCodes.Status400BadRequest, "缺少 userId");
            return null;
        }
        return body["userId"];
    }
}
