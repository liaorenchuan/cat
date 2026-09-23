namespace QPet.Core;

/// <summary>
/// WebSocket 帧(客户端 → 服务器方向)。
/// 服务器 → 客户端方向用匿名对象序列化(字段短名一致),两端共用本类保证入站格式一致。
/// </summary>
public sealed class WsFrame
{
    /// <summary>帧类型名统一见 Protocol 常量类(register / auth / chat / …)。</summary>
    public string T { get; set; } = "";

    // ---- 认证 ----
    /// <summary>账号(register / auth 用)。</summary>
    public string? Account { get; set; }

    /// <summary>密码(register / auth 用)。</summary>
    public string? Password { get; set; }

    /// <summary>昵称(register / profileUpdate 用)。</summary>
    public string? Nickname { get; set; }

    /// <summary>宠物缩放尺寸(petSizeUpdate 用:80-400)。</summary>
    public double PetSize { get; set; }

    // ---- 聊天 ----
    /// <summary>chat 用:0 私聊 / 1 群聊。</summary>
    public int ConvType { get; set; }

    /// <summary>chat 用:私聊 = 对方 UserId;群聊 = 群 ID。</summary>
    public string? ConvId { get; set; }

    /// <summary>chat 用:消息文本。</summary>
    public string? Text { get; set; }

    /// <summary>read 用:已读到哪条消息 Seq(游标推进)。</summary>
    public long Seq { get; set; }

    // ---- 好友 ----
    /// <summary>friendSearch 用:搜索关键字。</summary>
    public string? Keyword { get; set; }

    /// <summary>friendRequest 用:申请目标。</summary>
    public string? TargetUserId { get; set; }

    /// <summary>friendRespond 用:申请 ID。</summary>
    public long RequestId { get; set; }

    /// <summary>friendRespond 用:是否同意。</summary>
    public bool Accept { get; set; }

    /// <summary>friendRemark 用:备注内容。</summary>
    public string? Remark { get; set; }

    // ---- 群 ----
    /// <summary>groupCreate 用:群名。</summary>
    public string? Name { get; set; }

    /// <summary>groupCreate 用:拉入的成员 UserId 列表。</summary>
    public List<string>? MemberIds { get; set; }

    /// <summary>groupRename / groupJoin / groupAddMember / groupLeave 用:目标群。</summary>
    public string? GroupId { get; set; }

    // ---- 云端键值设置 ----
    /// <summary>setSetting 用:键。</summary>
    public string? Key { get; set; }

    /// <summary>setSetting 用:值(空串 = 清除)。</summary>
    public string? Value { get; set; }
}
