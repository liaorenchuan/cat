using System.Collections.Generic;

namespace QPet.Core;

/// <summary>
/// 本地离线缓存:正常登录时把云端数据(身份/快照/消息)落盘,离线登录时加载它直接进软件。
/// 全部字段为可序列化 POCO,与服务器契约同源(ChatModels.cs),不另建协议。
/// </summary>
public sealed class LocalCache
{
    /// <summary>所属账号(文件名标识,与 settings.json 的 Account 一致)。</summary>
    public string Account { get; set; } = "";

    /// <summary>当前身份(离线模式 MyUserId/MyNickname 来源)。</summary>
    public UserInfo MyUser { get; set; } = new();

    /// <summary>welcome 全量快照副本(好友/群/申请/已读/设置/未读)。</summary>
    public AuthSnapshot Snapshot { get; set; } = new();

    /// <summary>全部消息(与内存同样式按 Seq 升序,上限与 ChatView.MaxMessages 对齐)。</summary>
    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>已见消息游标(离线期间不再推进)。</summary>
    public long LastSeq { get; set; }

    /// <summary>落盘时间(展示/排查用)。</summary>
    public System.DateTimeOffset SavedAt { get; set; }
}
