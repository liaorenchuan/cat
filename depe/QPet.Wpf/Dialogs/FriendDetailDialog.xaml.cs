using System.Windows;
using System.Windows.Controls;
using QPet.Core;
using QPet.Ui;
using QPet.Ui.Dialogs;

namespace QPet.Wpf.Dialogs;

/// <summary>
/// 好友详情页:昵称/在线状态 + 账号(经 GetUserProfile 拉取) + 备注(可改) +
/// 💬 发消息(开会话) + ✂ 删除好友。备注修改走 SetFriendRemark,服务器持久化、仅自己可见。
/// </summary>
public partial class FriendDetailDialog : Window
{
    private readonly SyncClient _client;
    private readonly FriendInfo _friend;
    private readonly Action _onOpenChat;
    private bool _profileLoaded;

    public FriendDetailDialog(SyncClient client, FriendInfo friend, Action onOpenChat)
    {
        InitializeComponent();
        _client = client;
        _friend = friend;
        _onOpenChat = onOpenChat;

        // 头部
        var name = friend.Remark.Length > 0 ? friend.Remark : friend.Nickname;
        NameText.Text = name;
        ((TextBlock)AvatarText.Child).Text = name.Length > 0 ? name.Substring(0, 1) : "?";
        OnlineDot.Fill = friend.Online ? UiPalette.Green : System.Windows.Media.Brushes.Silver;
        OnlineText.Text = friend.Online ? "在线" : "离线";
        RemarkBox.Text = friend.Remark;

        Loaded += (_, _) =>
        {
            // 账号/昵称是好友公开资料,异步拉取(也是新人检测:好友已被删则直接提示)
            _client.UserProfileReceived += OnUserProfileReceived;
            _client.ErrorReceived += OnError;
            _client.GetUserProfile(friend.UserId);
        };
        Closed += (_, _) =>
        {
            _client.UserProfileReceived -= OnUserProfileReceived;
            _client.ErrorReceived -= OnError;
        };
    }

    private void OnUserProfileReceived(UserProfile profile)
    {
        if (profile.UserId != _friend.UserId)
            return; // 过期响应(详情页快速切换)
        _profileLoaded = true;
        AccountText.Text = $"账号: {profile.Account}";
        if (profile.Nickname.Length > 0 && _friend.Remark.Length == 0)
        {
            NameText.Text = profile.Nickname; // 无备注时显示服务器最新昵称
            ((TextBlock)AvatarText.Child).Text = profile.Nickname.Substring(0, 1);
        }
    }

    private void OnError(string reason)
    {
        // 好友已被删/资料拉取失败:提示但可继续操作(发消息/删除会由服务器再判)
        if (!_profileLoaded)
            ErrorText.Text = reason;
    }

    private void OnSaveRemark(object sender, RoutedEventArgs e)
    {
        var remark = RemarkBox.Text.Trim();
        if (remark.Length > 20)
        {
            ErrorText.Text = "备注最多 20 字";
            return;
        }
        _client.SetFriendRemark(_friend.UserId, remark);
        _friend.Remark = remark; // 本地直接生效(服务器只校验长度)
        var name = remark.Length > 0 ? remark : _friend.Nickname;
        NameText.Text = name;
        ((TextBlock)AvatarText.Child).Text = name.Length > 0 ? name.Substring(0, 1) : "?";
        ErrorText.Text = "备注已保存 ✓";
    }

    private void OnOpenChat(object sender, RoutedEventArgs e)
    {
        _onOpenChat();
        Close();
    }

    private void OnDeleteFriend(object sender, RoutedEventArgs e)
    {
        var name = NameText.Text;
        if (new ConfirmDialog("删除好友",
                $"确定删除好友「{name}」吗?删除后双方好友列表都会移除,聊天记录保留。",
                okText: "是", cancelText: "否") { Owner = this }.ShowDialog() != true)
            return;
        _client.SendDeleteFriend(_friend.UserId);
        Close(); // 列表由 ChatView 的 friendDeleted 事件刷新
    }
}
