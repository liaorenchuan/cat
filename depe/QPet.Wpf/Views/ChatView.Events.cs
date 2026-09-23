using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QPet.Core;
using QPet.Ui;

namespace QPet.Wpf.Views;
/// <summary>ChatView 分部:SyncClient 事件处理(均在 UI 线程,直接刷控件)。</summary>
public partial class ChatView
{
    // ---- SyncClient 事件(已在 UI 线程,直接刷控件) ----

    private void OnWelcome(AuthSnapshot snap) => ApplySnapshot(snap);

    /// <summary>云端设置回推(本账号其他端写入):群备注刷新显示(未读走独立 unreadUpdate 帧)。</summary>
    private void OnSettingUpdated(string key, string value)
    {
        if (key.StartsWith("GroupRemark.") && _convType == 1 && key == "GroupRemark." + _convId)
            ApplyGroupRemark();
    }

    private void OnUsersChanged(List<UserInfo> users)
    {
        // 在线状态刷新(好友列表圆点)
        var onlineIds = users.Select(u => u.UserId).ToHashSet();
        foreach (var f in _friends)
            f.Online = onlineIds.Contains(f.UserId);
        RefreshFriendList();
        RefreshConvList(); // 会话列表好友条目也有在线圆点
        if (_convOpen && _convType == 0)
            RefreshConvHeader(); // 打开私聊时:头部状态点即时刷新
    }

    private void OnRequestReceived(FriendRequest req)
    {
        _pendingFriendCount++;
        RefreshRequestBadge();
    }

    /// <summary>我的好友申请被处理:同意 = 已是好友(FriendAdded 已刷列表);拒绝 = 状态栏提示。</summary>
    private void OnFriendRequestResultReceived(long requestId, bool accepted, string fromUserId, string fromName)
    {
        StatusText.Text = accepted
            ? $"已和 {fromName} 成为好友,已在好友列表"
            : $"{fromName} 拒绝了你的好友申请";
    }

    /// <summary>我审批的申请已被处理:徽章减一(弹窗内由 RequestsDialog 自行刷新)。</summary>
    private void OnRequestHandled(long requestId)
    {
        if (_pendingFriendCount > 0)
            _pendingFriendCount--;
        RefreshRequestBadge();
    }

    private void OnFriendAdded(FriendInfo friend)
    {
        // 合并进现有列表,保留在线状态
        var existing = _friends.FirstOrDefault(x => x.UserId == friend.UserId);
        if (existing is null)
        {
            _friends.Add(friend);
            RefreshFriendList();
            RefreshConvList();
        }
    }

    /// <summary>新加群申请(我是群主/管理员):徽章加一,弹窗内列表实时增补。</summary>
    private void OnGroupRequestReceived(GroupRequest req)
    {
        _pendingGroupCount++;
        RefreshRequestBadge();
    }

    /// <summary>我的加群申请被处理:同意 = 群已在 groupUpdated 推送中加入列表;拒绝 = 提示。</summary>
    private void OnGroupRequestResultReceived(long requestId, bool accepted, string groupId, string groupName)
    {
        StatusText.Text = accepted
            ? $"已加入「{groupName}」,群聊已在消息列表"
            : $"「{groupName}」的加入申请被拒绝";
    }

    /// <summary>我审批的申请已处理:徽章减一(弹窗内列表自行刷新)。</summary>
    private void OnGroupRequestHandled(long requestId)
    {
        if (_pendingGroupCount > 0)
            _pendingGroupCount--;
        RefreshRequestBadge();
    }

    /// <summary>新群邀请(有人拉我进群):徽章加一,弹窗内列表实时增补。</summary>
    private void OnGroupInviteReceived(GroupInvite invite)
    {
        _pendingInviteCount++;
        RefreshRequestBadge();
    }

    /// <summary>我发出的邀请被处理:同意 = 群已在 groupUpdated 推送中刷新;拒绝 = 提示。</summary>
    private void OnGroupInviteResultReceived(long inviteId, bool accepted, string groupId, string groupName)
    {
        StatusText.Text = accepted
            ? $"「{groupName}」的邀请已同意,进群消息已推送"
            : $"「{groupName}」的邀请被拒绝";
    }

    /// <summary>我收到的邀请已处理(拒绝/群解散):徽章减一(弹窗内列表自行刷新)。</summary>
    private void OnGroupInviteHandled(long inviteId)
    {
        if (_pendingInviteCount > 0)
            _pendingInviteCount--;
        RefreshRequestBadge();
    }

    private void OnGroupUpdated(GroupInfo group)
    {
        var existing = _groups.FirstOrDefault(x => x.GroupId == group.GroupId);
        if (existing is not null)
        {
            // 全字段同步:成员数/群号/群名/管理员(改名/加人/退人/设管理都走这里)
            existing.Members = group.Members;
            existing.Num = group.Num;
            existing.Name = group.Name;
            existing.IsPublic = group.IsPublic;
            existing.OwnerId = group.OwnerId;
            existing.AdminIds = group.AdminIds;
        }
        else
        {
            _groups.Add(group); // 新群(自己刚建的群事件)
        }
        RefreshGroupList();
        RefreshConvList();
        if (_convType == 1 && _convId == group.GroupId)
            SelectConv(1, group.GroupId); // 刷新头部(改名/成员数)
    }

    private void OnProfileUpdated(string userId, string nickname)
    {
        var f = _friends.FirstOrDefault(x => x.UserId == userId);
        if (f is not null)
        {
            f.Nickname = nickname;
            RefreshFriendList();
            RefreshConvList();
            if (_convType == 0 && _convId == userId)
                SelectConv(0, userId); // 私聊头部显示最新昵称
        }
    }

    private void OnError(string reason) => StatusText.Text = reason;
}

/// <summary>时间分组日期栏(今天/昨天/yyyy年M月d日 星期x)。独立顶层类:嵌套类 XAML 无法引用。</summary>
public sealed class DateBar
{
    public string Text { get; set; } = "";
}

/// <summary>消息区条目模板选择:日期栏走灰字,气泡走头像+气泡。</summary>
public sealed class MessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DateBarTemplate { get; set; }
    public DataTemplate? BubbleTemplate { get; set; }
    public override DataTemplate SelectTemplate(object item, DependencyObject container) =>
        item is DateBar ? DateBarTemplate! : BubbleTemplate!;
}
