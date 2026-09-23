using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QPet.Core;
using QPet.Ui;
using QPet.Wpf.Dialogs;
using QPet.Ui.Dialogs;

namespace QPet.Wpf.Views;
/// <summary>ChatView 分部:好友/群聊列表与关系操作(删除好友/成员/退群/搜索)。</summary>
public partial class ChatView
{
    // ---- 好友页 ----

    private void RefreshFriendList()
    {
        _friendItems.Clear();
        foreach (var f in _friends)
        {
            var name = FriendDisplayName(f);
            _friendItems.Add(new FriendItem
            {
                UserId = f.UserId,
                Title = name,
                AvatarChar = name.Length > 0 ? name.Substring(0, 1) : "?",
                AvatarBrush = AvatarBrushFor(f.UserId),
                Online = f.Online && _client.IsConnected, // 断网/离线:全部置灰
                Sub = f.Remark.Length > 0 ? f.Nickname : "",
            });
        }
        // 注意:ItemsSource 必须赋新实例——WPF 依赖属性在引用相同时不触发刷新,
        // 同一 List 反复赋值会导致 OnWelcome 补快照时列表停留在旧状态(只有首条/空白)
        FriendEmptyText.Text = "还没有好友\n点击上方「搜索好友」添加";
        FriendEmptyText.Visibility = _friendItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FriendList.ItemsSource = _friendItems.ToList();
    }

    // ---- 群聊页 ----

    private void RefreshGroupList()
    {
        _groupItems.Clear();
        foreach (var g in _groups.OrderBy(g => g.GroupId == "public" ? 0 : 1))
        {
            var title = GroupDisplayName(g);
            _groupItems.Add(new GroupItem
            {
                GroupId = g.GroupId,
                Title = title,
                // 群号:公共群固定 000000,普通群递增(服务器分配,这里直接显示)
                Sub = g.Num.Length > 0 ? $"群号 {g.Num} · 成员 {g.Members.Count} 人"
                    : $"成员 {g.Members.Count} 人",
            });
        }
        GroupEmptyText.Visibility = _groupItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupList.ItemsSource = _groupItems.ToList();
    }


    /// <summary>删除好友:确认后发送(服务器删除双方关系,推 friendDeleted 给双方)。</summary>
    private void OnDeleteFriend(object sender, RoutedEventArgs e)
    {
        var f = _friends.FirstOrDefault(x => x.UserId == _convId);
        if (f is null)
            return;
        var name = FriendDisplayName(f);
        if (new ConfirmDialog("删除好友",
                $"确定删除好友「{name}」吗?删除后双方好友列表都会移除,聊天记录保留。",
                okText: "是", cancelText: "否") { Owner = Window.GetWindow(this) }.ShowDialog() != true)
            return;
        if (!_client.IsConnected) { StatusText.Text = "没连接服务器"; return; }
        _client.SendDeleteFriend(_convId);
    }

    /// <summary>好友被删(对方删的我,或我删的对方):从列表移除,当前会话切回公共群。</summary>
    private void OnFriendDeleted(string userId)
    {
        _friends.RemoveAll(x => x.UserId == userId);
        if (_convType == 0 && _convId == userId)
            SelectConv(1, "public");
        // 会话没了,未读/预览/游标一并清理
        _unread.Remove($"{Protocol.ConvKeyPrivate}{userId}");
        _lastPreview.Remove($"{Protocol.ConvKeyPrivate}{userId}");
        _convMaxSeq.Remove($"{Protocol.ConvKeyPrivate}{userId}");
        RefreshConvList();
        RefreshFriendList();
    }

    /// <summary>查看群成员:弹窗列出群内各个人(昵称 + 在线状态),成员可拉好友进群。</summary>
    private void OnMembers(object sender, RoutedEventArgs e)
    {
        var g = _groups.FirstOrDefault(x => x.GroupId == _convId);
        if (g is null)
            return;
        if (!_client.IsConnected) { StatusText.Text = "没连接服务器"; return; } // 弹窗内有拉人/审批等网络操作
        // 群主/管理员视角传角色 + AdminIds:群主显示设管理员按钮(仅群主可设);
        // 移出按钮群主可移任何人,管理员可移普通成员
        new MembersDialog(g.GroupId, g.Name, g.Members, _client,
            isOwner: g.OwnerId == _client.MyUser.UserId,
            adminIds: g.AdminIds, ownerId: g.OwnerId,
            isAdmin: g.AdminIds.Contains(_client.MyUser.UserId)) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    /// <summary>退出群聊(群主退出 = 解散该群,全员收 groupRemoved)。</summary>
    private void OnLeaveGroup(object sender, RoutedEventArgs e)
    {
        var g = _groups.FirstOrDefault(x => x.GroupId == _convId);
        if (g is null)
            return;
        // 群主信息在客户端快照里没有,直接问通用文案;解散由服务器处理
        if (new ConfirmDialog("退出群聊",
                $"确定退出群聊「{g.Name}」吗?\n(群主退出将解散该群,群聊记录一并删除)",
                okText: "是", cancelText: "否") { Owner = Window.GetWindow(this) }.ShowDialog() != true)
            return;
        if (!_client.IsConnected) { StatusText.Text = "没连接服务器"; return; }
        _client.LeaveGroup(_convId);
    }

    /// <summary>群被解散 / 我(被)移出群聊:从列表移除,当前会话切回公共群;reason 提示原因。</summary>
    private void OnGroupRemoved(string groupId, string reason)
    {
        _groups.RemoveAll(x => x.GroupId == groupId);
        if (_convType == 1 && _convId == groupId)
            SelectConv(1, "public");
        // 会话没了,未读/预览/游标一并清理
        _unread.Remove($"{Protocol.ConvKeyGroup}{groupId}");
        _lastPreview.Remove($"{Protocol.ConvKeyGroup}{groupId}");
        _convMaxSeq.Remove($"{Protocol.ConvKeyGroup}{groupId}");
        RefreshConvList();
        RefreshGroupList();
        if (reason.Length > 0)
            StatusText.Text = reason;
    }

    /// <summary>按群号搜索加群:弹窗内搜索 + 展示结果 + 加入(结果由对话框自己订阅处理)。</summary>
    private void OnSearchGroup(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) { StatusText.Text = "没连接服务器"; return; } // 弹窗内有搜索/加入网络操作
        new SearchGroupDialog(_client) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}
