// 注意:本文件在 Server/Wpf 间复制维护,序列化契约,改字段需同步各份。
namespace QPet.Core;

/// <summary>
/// 局域网用户信息。服务器维护在线表,档案持久化到数据库。
/// 身份靠 UserId(注册时生成),昵称/账号均可展示。
/// </summary>
public sealed class UserInfo
{
    public string UserId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public long LastSeenMs { get; set; } // 最近活跃(Unix 毫秒,服务器时间)
}

/// <summary>一条好友关系(含各自视角的备注)。</summary>
public sealed class FriendInfo
{
    public string UserId { get; set; } = "";
    public string Account { get; set; } = ""; // 对方账号(展示用,内部 userId 不友好)
    public string Nickname { get; set; } = "";
    public string Remark { get; set; } = ""; // 我方给他起的备注(空 = 未设置)
    public bool Online { get; set; }
}

/// <summary>群成员(展示用,Online 由服务器查询时填充)。</summary>
public sealed class GroupMember
{
    public string UserId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string Account { get; set; } = ""; // 管理端群管理展示用(客户端不需要)
    public bool Online { get; set; }
}

/// <summary>一个群聊:公共群聊(IsPublic)或好友自建群。</summary>
public sealed class GroupInfo
{
    public string GroupId { get; set; } = "";
    public string Num { get; set; } = ""; // 群号(短数字,搜索加群用)
    public string Name { get; set; } = "";
    public bool IsPublic { get; set; }
    public string OwnerId { get; set; } = ""; // 群主 UserId
    public List<GroupMember> Members { get; set; } = new();
    public List<string> AdminIds { get; set; } = new(); // 管理员 UserId 列表(群主 + 管理员可审批加群申请)
}

/// <summary>群搜索结果(展示 + 加入按钮状态)。</summary>
public sealed class GroupSearchResult
{
    public string GroupId { get; set; } = "";
    public string Num { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsPublic { get; set; }
    public int MemberCount { get; set; }
    public bool IsMember { get; set; }
    public bool IsOwner { get; set; }
    public bool HasPendingRequest { get; set; } // 我已申请该群且未审批(按钮显示"已申请")
}

/// <summary>好友搜索结果(展示 + 添加按钮状态)。</summary>
public sealed class UserSearchResult
{
    public string UserId { get; set; } = "";
    public string Account { get; set; } = "";
    public string Nickname { get; set; } = "";
    public bool IsFriend { get; set; }
    public bool HasPendingRequest { get; set; }
}

/// <summary>用户资料(群聊里点头像查看):身份信息 + 与我的好友/申请状态。</summary>
public sealed class UserProfile
{
    public string UserId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string Account { get; set; } = "";
    public bool IsMe { get; set; }
    public bool IsFriend { get; set; }
    public bool HasPendingRequest { get; set; }
}

/// <summary>welcome 帧的全量快照:好友/群/待处理申请(客户端首次进入页面用)。</summary>
public sealed class AuthSnapshot
{
    public List<FriendInfo> Friends { get; set; } = new();
    public List<GroupInfo> Groups { get; set; } = new();
    public List<FriendRequest> Requests { get; set; } = new();
    public List<GroupRequest> GroupRequests { get; set; } = new(); // 待我审批的加群申请(我是群主/管理员)
    public List<GroupInvite> GroupInvites { get; set; } = new(); // 我收到的群邀请(待同意;被拉进群需同意)
    public List<ReadState> ReadStates { get; set; } = new(); // 各会话其他人的已读游标(重连/重启后已读标记不丢)
    public Dictionary<string, string> Settings { get; set; } = new(); // 云端键值设置(群备注等,换设备登录即有)
    public Dictionary<string, int> UnreadCounts { get; set; } = new(); // 未读红点(服务器权威,key = 会话视角键 "0:对方"/"1:群")
}

/// <summary>一条加群申请(待处理/已处理),由群主或管理员审批。</summary>
public sealed class GroupRequest
{
    public long ReqId { get; set; }
    public string GroupId { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string FromUserId { get; set; } = "";
    public string FromName { get; set; } = "";
    public long CreatedAtMs { get; set; }
}

/// <summary>一条群邀请(被拉进群,需被邀者同意):有人邀请我加入某个群。</summary>
public sealed class GroupInvite
{
    public long InviteId { get; set; }
    public string GroupId { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string FromUserId { get; set; } = "";
    public string FromName { get; set; } = "";
    public long CreatedAtMs { get; set; }
}

/// <summary>一条好友申请(待处理/已处理)。</summary>
public sealed class FriendRequest
{
    public long ReqId { get; set; }
    public string FromUserId { get; set; } = "";
    public string FromName { get; set; } = "";
    public string ToUserId { get; set; } = "";
    public string Text { get; set; } = "";
    public long CreatedAtMs { get; set; }
}

/// <summary>
/// 一条聊天消息。Seq/Time 由服务器赋值。
/// ConvType:0 = 私聊(ConvId = 对方 UserId),1 = 群聊(ConvId = 群 ID)。
/// </summary>
public sealed class ChatMessage
{
    public long Seq { get; set; }
    public string FromUserId { get; set; } = "";
    public string FromName { get; set; } = "";
    public int ConvType { get; set; }
    public string ConvId { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public int Type { get; set; } // 0 = 普通消息,1 = 系统消息(成员变动提示,FromUserId/FromName 为空)
}

/// <summary>
/// 已读游标快照(welcome / readStates 帧):某用户在某会话已读到哪条 Seq。
/// ConvId 是「对当前用户视角」的会话 ID(私聊 = 对方 UserId,群聊 = 群 ID)。
/// </summary>
public sealed class ReadState
{
    public int ConvType { get; set; }
    public string ConvId { get; set; } = "";
    public string ReaderUserId { get; set; } = ""; // 谁读的(自己发出的消息被谁读了)
    public long Seq { get; set; }
}

/// <summary>已读回执推送(read 帧):ReaderUserId 在会话中已读到 Seq。</summary>
public sealed class ReadReceipt
{
    public int ConvType { get; set; }
    public string ConvId { get; set; } = "";
    public string FromUserId { get; set; } = "";
    public long Seq { get; set; }
}
