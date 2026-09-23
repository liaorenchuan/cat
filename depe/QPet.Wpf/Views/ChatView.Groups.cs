using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QPet.Core;
using QPet.Ui;
using QPet.Wpf.Dialogs;

namespace QPet.Wpf.Views;
/// <summary>ChatView 分部:会话头部操作(备注/改名)、用户主页、个人主页与弹窗入口。</summary>
public partial class ChatView
{
    // ---- 会话头部操作(备注 / 改名) ----

    private void OnConvAction(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (_convType == 0)
        {
            var dlg = new PromptDialog("好友备注", "给好友起个备注(空 = 取消备注,最多 20 字)") { Owner = owner };
            var current = _friends.FirstOrDefault(x => x.UserId == _convId)?.Remark ?? "";
            dlg.Value = current;
            if (dlg.ShowDialog() == true)
            {
                var remark = dlg.Value.Trim();
                if (remark.Length > 20)
                {
                    StatusText.Text = "备注最多 20 字";
                    return;
                }
                if (!_client.IsConnected) { StatusText.Text = "没连接服务器"; return; }
                _client.SetFriendRemark(_convId, remark);
                // 备注不广播,本地直接刷(服务器只校验长度,超长会回 error)
                var f = _friends.FirstOrDefault(x => x.UserId == _convId);
                if (f is not null)
                {
                    f.Remark = remark;
                    RefreshConvList();
                    RefreshFriendList();
                    SelectConv(0, _convId);
                }
            }
        }
        else if (_convType == 1 && _convId != "public")
        {
            var g = _groups.FirstOrDefault(x => x.GroupId == _convId);
            var dlg = new PromptDialog("群聊改名", "输入新的群名称(最多 20 字)") { Owner = owner };
            dlg.Value = g?.Name ?? "";
            if (dlg.ShowDialog() == true)
            {
                var name = dlg.Value.Trim();
                if (name.Length == 0)
                {
                    // 输入为空:不改名,群名保持原来的名字
                    StatusText.Text = "群名不能为空,已保持原群名";
                    return;
                }
                if (name.Length > 20)
                {
                    StatusText.Text = "群名最多 20 字";
                    return;
                }
                if (!_client.IsConnected) { StatusText.Text = "没连接服务器"; return; }
                _client.RenameGroup(_convId, name); // 结果由 GroupUpdated 事件回刷
            }
        }
    }

    /// <summary>读云端群备注(welcome 快照 / settingUpdated 回推缓存,空 = 无备注)。只有自己可见。</summary>
    private string GroupRemarkOf(string groupId) =>
        _client.LastSnapshot.Settings.GetValueOrDefault("GroupRemark." + groupId) ?? "";

    /// <summary>备注按钮文字:有备注时显示备注名,否则"✏ 备注"。</summary>
    private void UpdateGroupRemarkButton()
    {
        var remark = GroupRemarkOf(_convId);
        GroupRemarkButton.Content = remark.Length > 0 ? $"✏ 备注:{remark}" : "✏ 备注";
    }

    /// <summary>群备注(改名右侧):输入预填当前备注,空 = 取消备注;云端保存(换设备登录也有),按钮文字显示备注名。</summary>
    private void OnGroupRemark(object sender, RoutedEventArgs e)
    {
        if (_convType != 1 || _convId == "public")
            return;
        var owner = Window.GetWindow(this);
        var dlg = new PromptDialog("群备注", "给群起个备注(空 = 取消备注,最多 20 字)") { Owner = owner };
        dlg.Value = GroupRemarkOf(_convId);
        if (dlg.ShowDialog() != true)
            return;
        var remark = dlg.Value.Trim();
        if (remark.Length > 20)
        {
            StatusText.Text = "备注最多 20 字";
            return;
        }
        if (!_client.IsConnected) { StatusText.Text = "没连接服务器"; return; }
        // 云端保存:服务器落库并回推(回推会更新快照缓存,这里乐观刷新 UI 即时生效)
        _client.SetSetting("GroupRemark." + _convId, remark);
        _client.LastSnapshot.Settings["GroupRemark." + _convId] = remark;
        ApplyGroupRemark();
        StatusText.Text = remark.Length > 0 ? $"群备注已保存: {remark} ✓" : "群备注已取消";
    }

    /// <summary>群备注变更后刷新:消息左侧列表 + 群聊页列表 + 消息栏标题 + 按钮文字。</summary>
    private void ApplyGroupRemark()
    {
        var g = _groups.FirstOrDefault(x => x.GroupId == _convId);
        if (g is not null)
            ConvTitleText.Text = GroupDisplayName(g);
        RefreshConvList();
        RefreshGroupList();
        UpdateGroupRemarkButton();
    }

    // ---- 群聊头像 → 用户主页 ----

    /// <summary>群聊消息气泡上的头像:点开对方主页(资料 + 加好友)。</summary>
    private void OnMsgAvatarClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MsgItem item })
            return;
        ShowUserProfile(item.Msg.FromUserId);
    }

    private void ShowUserProfile(string userId)
    {
        if (!_client.IsConnected) { StatusText.Text = "没连接服务器"; return; }
        _profilePendingId = userId;
        _client.GetUserProfile(userId); // 结果异步回 UserProfileReceived;不存在回 error
    }

    private void OnUserProfileReceived(UserProfile profile)
    {
        if (profile.UserId != _profilePendingId)
            return; // 过期响应(快速切换头像)
        _profilePendingId = "";

        UserAvatarText.Text = profile.Nickname.Length > 0 ? profile.Nickname.Substring(0, 1) : "?";
        UserNameText.Text = profile.Nickname;
        UserAccountText.Text = $"账号: {profile.Account}";

        if (profile.IsMe)
        {
            UserAddButton.Visibility = Visibility.Collapsed;
            UserHintText.Text = "这是你自己";
        }
        else if (profile.IsFriend)
        {
            UserAddButton.Visibility = Visibility.Collapsed;
            UserHintText.Text = "已经是好友";
        }
        else if (profile.HasPendingRequest)
        {
            UserAddButton.Visibility = Visibility.Collapsed;
            UserHintText.Text = "申请已发送,等待对方同意";
        }
        else
        {
            _profileTargetId = profile.UserId;
            UserAddButton.Visibility = Visibility.Visible;
            UserHintText.Text = "";
        }
        UserPopup.IsOpen = true;
    }

    private void OnUserAddFriend(object sender, RoutedEventArgs e)
    {
        if (_profileTargetId.Length == 0)
            return;
        if (!_client.IsConnected) { StatusText.Text = "没连接服务器"; return; }
        _client.SendFriendRequest(_profileTargetId, "");
        UserAddButton.Visibility = Visibility.Collapsed;
        UserHintText.Text = "申请已发送,等待对方同意";
        _profileTargetId = "";
    }

    // ---- 个人主页 ----

    /// <summary>左栏个人卡悬停反馈(背景提亮一档,提示可点开主页)。</summary>
    private void OnAvatarHoverIn(object sender, MouseEventArgs e) =>
        AvatarCard.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xE0));

    private void OnAvatarHoverOut(object sender, MouseEventArgs e) =>
        AvatarCard.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xEF));

    private void OnAvatarClick(object sender, MouseButtonEventArgs e)
    {
        ProfileNameText.Text = _app.MyNickname;
        ProfileAccountText.Text = $"账号: {_app.MyAccount}";
        ProfilePopup.IsOpen = true;
    }

    private void OnChangeNickname(object sender, RoutedEventArgs e)
    {
        ProfilePopup.IsOpen = false;
        var dlg = new PromptDialog("修改昵称", "新昵称(1-20 字)") { Owner = Window.GetWindow(this) };
        dlg.Value = _app.MyNickname;
        if (dlg.ShowDialog() == true)
        {
            var nick = dlg.Value.Trim();
            if (nick.Length == 0 || nick.Length > 20)
            {
                StatusText.Text = "昵称需为 1-20 字";
                return;
            }
            if (!_client.IsConnected) { StatusText.Text = "没连接服务器"; return; }
            _client.UpdateNickname(nick);
            // 服务器 profileUpdated 只推好友,自己这里本地回刷
            _client.MyUser.Nickname = nick;
            MyNickText.Text = nick;
            AvatarText.Text = nick.Length > 0 ? nick.Substring(0, 1) : "?";
        }
    }

    private void OnLogout(object sender, RoutedEventArgs e)
    {
        ProfilePopup.IsOpen = false;
        _app.Logout();
    }

    // ---- 弹窗 ----

    private void OnAddFriend(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) { StatusText.Text = "没连接服务器"; return; } // 弹窗内有搜索网络操作
        new AddFriendDialog(_client) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    /// <summary>好友页通知:只看好友申请。</summary>
    private void OnFriendRequests(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) { StatusText.Text = "没连接服务器"; return; } // 弹窗内有审批网络操作
        new RequestsDialog(_client, 0) { Owner = Window.GetWindow(this) }.ShowDialog();
        RefreshRequestBadge();
    }

    /// <summary>群聊页通知:只看群申请(群主/管理员审批)。</summary>
    private void OnGroupRequests(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) { StatusText.Text = "没连接服务器"; return; } // 弹窗内有审批网络操作
        new RequestsDialog(_client, 1) { Owner = Window.GetWindow(this) }.ShowDialog();
        RefreshRequestBadge();
    }

    /// <summary>群聊页通知:只看群邀请(有人拉我进群,需我同意)。</summary>
    private void OnGroupInvites(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) { StatusText.Text = "没连接服务器"; return; } // 弹窗内有审批网络操作
        new RequestsDialog(_client, 2) { Owner = Window.GetWindow(this) }.ShowDialog();
        RefreshRequestBadge();
    }

    private void OnCreateGroup(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) { StatusText.Text = "没连接服务器"; return; } // 弹窗内有创建网络操作
        new CreateGroupDialog(_client, _friends) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void RefreshRequestBadge()
    {
        FriendRequestsButton.Content = _pendingFriendCount > 0 ? "📮 好友申请 ●" : "📮 好友申请";
        GroupRequestsButton.Content = _pendingGroupCount > 0 ? "📮 群申请 ●" : "📮 群申请";
        GroupInvitesButton.Content = _pendingInviteCount > 0 ? "📮 群邀请 ●" : "📮 群邀请";
        // 底部页签红点:好友页签 = 好友申请数,群聊页签 = 群申请 + 群邀请(>99 显示 99+)
        FriendTabBadge.Visibility = _pendingFriendCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        FriendTabBadgeText.Text = _pendingFriendCount > 99 ? "99+" : _pendingFriendCount.ToString();
        var groupBadge = _pendingGroupCount + _pendingInviteCount;
        GroupTabBadge.Visibility = groupBadge > 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupTabBadgeText.Text = groupBadge > 99 ? "99+" : groupBadge.ToString();
    }
}
