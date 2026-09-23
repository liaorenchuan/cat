// 注意:本文件在 Server/Admin/Wpf 间复制维护,改协议值需同步各份。
namespace QPet.Core;

/// <summary>
/// 跨端协议常量:帧类型名 / 会话键前缀 / 默认端口。
/// 值即线上协议(客户端/服务器以字符串字面量约定),两端共用一份,勿改。
/// </summary>
public static class Protocol
{
    /// <summary>IM 服务器默认端口。</summary>
    public const int DefaultPort = 54321;

    /// <summary>私聊会话键前缀(用户视角 "0:对方UserId",服务端路由 "0:a:b")。</summary>
    public const string ConvKeyPrivate = "0:";

    /// <summary>群聊会话键前缀("1:群ID")。</summary>
    public const string ConvKeyGroup = "1:";

    // ---- 双向共用 ----
    /// <summary>发消息(客户端 → 服务器;服务器 → 客户端广播)。</summary>
    public const string Chat = "chat";

    /// <summary>已读回执(双向)。</summary>
    public const string Read = "read";

    /// <summary>错误(服务器 → 客户端)。</summary>
    public const string Error = "error";

    // ---- 客户端 → 服务器 ----
    /// <summary>注册。</summary>
    public const string Register = "register";

    /// <summary>登录。</summary>
    public const string Auth = "auth";

    /// <summary>搜索用户。</summary>
    public const string FriendSearch = "friendSearch";

    /// <summary>查看用户资料。</summary>
    public const string UserProfile = "userProfile";

    /// <summary>删除好友。</summary>
    public const string FriendDelete = "friendDelete";

    /// <summary>发好友申请。</summary>
    public const string FriendRequest = "friendRequest";

    /// <summary>处理好友申请。</summary>
    public const string FriendRespond = "friendRespond";

    /// <summary>设置好友备注。</summary>
    public const string FriendRemark = "friendRemark";

    /// <summary>创建群。</summary>
    public const string GroupCreate = "groupCreate";

    /// <summary>群改名。</summary>
    public const string GroupRename = "groupRename";

    /// <summary>搜索群。</summary>
    public const string GroupSearch = "groupSearch";

    /// <summary>加入群(公共群)。</summary>
    public const string GroupJoin = "groupJoin";

    /// <summary>退出群。</summary>
    public const string GroupLeave = "groupLeave";

    /// <summary>发加群申请。</summary>
    public const string GroupRequestSend = "groupRequestSend";

    /// <summary>审批加群申请。</summary>
    public const string GroupRequestRespond = "groupRequestRespond";

    /// <summary>设置/取消群管理员。</summary>
    public const string GroupAdminSet = "groupAdminSet";

    /// <summary>发群邀请(拉好友进群,被邀者同意后加入)。</summary>
    public const string GroupInviteSend = "groupInviteSend";

    /// <summary>审批群邀请(同意/拒绝)。</summary>
    public const string GroupInviteRespond = "groupInviteRespond";

    /// <summary>群主移出成员。</summary>
    public const string GroupRemoveMember = "groupRemoveMember";

    /// <summary>更新昵称。</summary>
    public const string ProfileUpdate = "profileUpdate";

    /// <summary>更新宠物尺寸。</summary>
    public const string PetSizeUpdate = "petSizeUpdate";

    /// <summary>云端键值设置。</summary>
    public const string SetSetting = "setSetting";

    /// <summary>退出登录。</summary>
    public const string Logout = "logout";

    /// <summary>心跳。</summary>
    public const string Ping = "ping";

    // ---- 服务器 → 客户端 ----
    /// <summary>登录成功(welcome 快照)。</summary>
    public const string AuthOk = "authOk";

    /// <summary>登录成功附带的全量快照。</summary>
    public const string Welcome = "welcome";

    /// <summary>在线用户列表。</summary>
    public const string Users = "users";

    /// <summary>用户搜索结果。</summary>
    public const string FriendSearchResult = "friendSearchResult";

    /// <summary>用户资料结果。</summary>
    public const string UserProfileResult = "userProfileResult";

    /// <summary>收到新好友申请。</summary>
    public const string FriendRequestReceived = "friendRequestReceived";

    /// <summary>好友申请有结果。</summary>
    public const string FriendRequestResult = "friendRequestResult";

    /// <summary>好友申请被处理。</summary>
    public const string FriendRequestHandled = "friendRequestHandled";

    /// <summary>成为好友。</summary>
    public const string FriendAdded = "friendAdded";

    /// <summary>好友被删除。</summary>
    public const string FriendDeleted = "friendDeleted";

    /// <summary>群信息更新。</summary>
    public const string GroupUpdated = "groupUpdated";

    /// <summary>群搜索结果。</summary>
    public const string GroupSearchResult = "groupSearchResult";

    /// <summary>群被解散/移出。</summary>
    public const string GroupRemoved = "groupRemoved";

    /// <summary>收到加群申请。</summary>
    public const string GroupRequestReceived = "groupRequestReceived";

    /// <summary>加群申请有结果。</summary>
    public const string GroupRequestResult = "groupRequestResult";

    /// <summary>加群申请被处理。</summary>
    public const string GroupRequestHandled = "groupRequestHandled";

    /// <summary>收到新群邀请(被拉进群,需同意)。</summary>
    public const string GroupInviteReceived = "groupInviteReceived";

    /// <summary>群邀请有结果(同意/拒绝)。</summary>
    public const string GroupInviteResult = "groupInviteResult";

    /// <summary>群邀请被处理。</summary>
    public const string GroupInviteHandled = "groupInviteHandled";

    /// <summary>宠物尺寸已更新。</summary>
    public const string PetSizeUpdated = "petSizeUpdated";

    /// <summary>云端设置已更新。</summary>
    public const string SettingUpdated = "settingUpdated";

    /// <summary>未读红点更新。</summary>
    public const string UnreadUpdate = "unreadUpdate";

    /// <summary>用户昵称变更。</summary>
    public const string ProfileUpdated = "profileUpdated";

    /// <summary>心跳应答。</summary>
    public const string Pong = "pong";
}
