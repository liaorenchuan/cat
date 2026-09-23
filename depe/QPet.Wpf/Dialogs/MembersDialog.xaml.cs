using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QPet.Core;
using QPet.Ui;
using QPet.Ui.Dialogs;

namespace QPet.Wpf.Dialogs;

/// <summary>
/// 群成员弹窗:列出群内各个人(昵称 + 在线状态圆点),标题为群名。
/// 成员行可加好友:自己标"我"、已是好友标"好友",其余显示"＋ 好友"按钮,
/// 点击发送好友申请(按钮变"已发送"禁用)。
/// 底部可拉好友进群:下拉我的好友(未在群内的),点"拉入"后服务器广播 groupUpdated,
/// 群成员列表由 ChatView 刷新(对话框内即时更新列表)。
/// 群主视角:非群主成员行显示"设为/取消管理"按钮(设置管理员审批加群申请用)。
/// 移出成员:群主可移任何人(除自己/群主),管理员可移普通成员(除自己/群主/其他管理员)。
/// </summary>
public partial class MembersDialog : Window
{
    private readonly SyncClient _client;
    private readonly string _groupId;
    private readonly bool _isOwner;
    private readonly bool _isAdmin;
    private readonly string _ownerId;
    private HashSet<string> _admins = new(); // 群管理员 UserId 集合(groupUpdated 刷新)

    /// <summary>可拉入的好友:我的好友 - 已在群内的(昵称/备注展示)。</summary>
    private sealed class FriendOption
    {
        public string UserId { get; set; } = "";
        public string Title { get; set; } = "";
    }

    public MembersDialog(string groupId, string groupName, List<GroupMember> members, SyncClient client,
        bool isOwner = false, List<string>? adminIds = null, string ownerId = "", bool isAdmin = false)
    {
        InitializeComponent();
        _client = client;
        _groupId = groupId;
        _isOwner = isOwner;
        _isAdmin = isAdmin;
        // 群主由调用方传入(welcome 快照可能是旧数据,登录后才建的群在快照里找不到,群主会误显示加好友按钮)
        _ownerId = ownerId;
        _admins = adminIds?.ToHashSet() ?? new HashSet<string>();
        Title = $"{groupName} · 群成员";

        // 拉好友失败(不是好友等)会回 error:恢复按钮,提示原因
        Loaded += (_, _) =>
        {
            _client.ErrorReceived += OnError;
            _client.GroupUpdated += OnGroupUpdated; // 被邀者同意后实时刷新成员列表
        };
        Closed += (_, _) =>
        {
            _client.ErrorReceived -= OnError;
            _client.GroupUpdated -= OnGroupUpdated;
        };

        RefreshMembers(members);
    }

    /// <summary>重建成员列表 + 可拉好友下拉(打开/被邀者同意/成员变动时调用)。</summary>
    private void RefreshMembers(List<GroupMember> members)
    {
        var snap = _client.LastSnapshot;
        var friendIds = snap.Friends.Select(f => f.UserId).ToHashSet();
        // 排序:群主置顶,然后是群管理员,再是普通成员(同权重保持原顺序)
        var ordered = members
            .OrderBy(m => m.UserId == _ownerId ? 0 : _admins.Contains(m.UserId) ? 1 : 2)
            .ToList();
        MemberList.ItemsSource = ordered
            .Select(m => new MemberItem(m,
                isMe: m.UserId == _client.MyUser.UserId,
                isFriend: friendIds.Contains(m.UserId),
                isOwnerRole: _isOwner,
                isAdminRole: _isAdmin,
                ownerId: _ownerId,
                isAdmin: _admins.Contains(m.UserId)))
            .ToList();
        CountText.Text = $"共 {members.Count} 人";

        // 可拉入的好友:我的好友 - 已在群内的
        var inGroup = members.Select(m => m.UserId).ToHashSet();
        var options = snap.Friends
            .Where(f => !inGroup.Contains(f.UserId))
            .Select(f => new FriendOption
            {
                UserId = f.UserId,
                Title = f.Remark.Length > 0 ? f.Remark : f.Nickname,
            })
            .ToList();
        // 拉人面板始终显示:有可拉好友可选;没有则禁用并提示
        // (防"拉人选项时有时无"的困惑——没有不在群里的好友时之前整个面板隐藏)
        if (options.Count > 0)
        {
            FriendBox.ItemsSource = options;
            FriendBox.SelectedIndex = 0;
            FriendBox.IsEnabled = true;
            InviteButton.IsEnabled = true;
            InviteHintText.Text = "";
        }
        else
        {
            FriendBox.ItemsSource = null;
            FriendBox.IsEnabled = false;
            InviteButton.IsEnabled = false;
            InviteHintText.Text = "没有可拉入的好友";
        }
        InvitePanel.Visibility = Visibility.Visible;
    }

    /// <summary>被邀者同意后服务器广播 groupUpdated:本群成员列表实时刷新(新成员出现 + 可拉下拉更新)。</summary>
    private void OnGroupUpdated(GroupInfo group)
    {
        if (group.GroupId != _groupId)
            return;
        _admins = group.AdminIds?.ToHashSet() ?? new HashSet<string>();
        RefreshMembers(group.Members);
    }

    private void OnMemberAdd(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MemberItem item })
            return;
        _client.SendFriendRequest(item.UserId, "");
        // 发送成功与否由服务器决定;本地先置"已发送",重复点击无害(服务器去重)
        if (sender is Button btn)
        {
            btn.Content = "已发送";
            btn.IsEnabled = false;
        }
    }

    /// <summary>群主设置/取消管理员:发送后服务器广播 groupUpdated(含 AdminIds),本地翻转状态。</summary>
    private void OnToggleAdmin(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MemberItem item })
            return;
        _client.SetGroupAdmin(_groupId, item.UserId, !item.IsAdmin);
        item.ToggleAdmin();
        if (sender is Button btn)
            btn.Content = item.IsAdmin ? "取消管理" : "设为管理";
    }

    /// <summary>群主移出成员:确认后发送,被移者收 groupRemoved 提示,剩余成员收 groupUpdated;本地乐观移除。</summary>
    private void OnKickMember(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MemberItem item })
            return;
        if (new ConfirmDialog("移出成员", $"把「{item.Nickname}」移出群聊?") { Owner = this }.ShowDialog() != true)
            return;
        _client.RemoveGroupMember(_groupId, item.UserId);
        // 乐观移除:服务器广播 groupUpdated 时 ChatView 会整体刷新;这里先移除防残留
        var items = MemberList.ItemsSource.Cast<MemberItem>()
            .Where(x => x.UserId != item.UserId).ToList();
        MemberList.ItemsSource = items;
        CountText.Text = $"共 {items.Count} 人";
    }

    /// <summary>拉好友进群(邀请制):发邀请,对方同意后服务器广播 groupUpdated,本弹窗实时刷新成员列表。</summary>
    private void OnInvite(object sender, RoutedEventArgs e)
    {
        if (FriendBox.SelectedItem is not FriendOption f)
            return;
        InviteButton.IsEnabled = false; // 防重复点击;失败时 OnError 恢复并提示
        InviteHintText.Text = "";
        _client.SendGroupInvite(_groupId, f.UserId);
        // 邀请制:对方同意才入群;本地只提示已发出邀请(不同意不出现成员列表)
        InviteHintText.Text = "已发出邀请,等待对方同意";
    }

    /// <summary>拉好友失败(服务器拒绝,如不是好友):恢复按钮,显示原因。</summary>
    private void OnError(string reason)
    {
        InviteButton.IsEnabled = true;
        InviteHintText.Text = reason;
    }

    /// <summary>成员行:WPF 绑定只认属性,在线圆点/状态文本/按钮显隐都经属性换算。</summary>
    private sealed class MemberItem
    {
        public string UserId { get; }
        public string Nickname { get; }
        public bool IsAdmin { get; private set; }
        public Brush DotBrush { get; }
        public string ActionText { get; } = "";
        public Visibility ActionTextVisibility { get; } = Visibility.Collapsed;
        public Visibility AddButtonVisibility { get; } = Visibility.Visible;
        public Visibility AdminButtonVisibility { get; } = Visibility.Collapsed;
        public string AdminButtonText { get; } = "";
        public Visibility KickButtonVisibility { get; } = Visibility.Collapsed;

        public MemberItem(GroupMember m, bool isMe, bool isFriend, bool isOwnerRole, bool isAdminRole,
            string ownerId, bool isAdmin)
        {
            UserId = m.UserId;
            Nickname = m.Nickname;
            IsAdmin = isAdmin;
            DotBrush = m.Online
                ? UiPalette.Green
                : new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));
            if (isMe)
            {
                ActionText = "我";
                ActionTextVisibility = Visibility.Visible;
                AddButtonVisibility = Visibility.Collapsed;
            }
            else
            {
                // 角色标记:群主优先于管理员(AdminIds 可能含群主,不能显示成"管理");
                // 群主/管理员也保留加好友按钮,只有自己/已是好友才隐藏按钮
                if (UserId == ownerId)
                    ActionText = "群主";
                else if (isAdmin)
                    ActionText = "管理";
                if (isFriend)
                {
                    if (ActionText.Length == 0)
                        ActionText = "好友";
                    ActionTextVisibility = Visibility.Visible;
                    AddButtonVisibility = Visibility.Collapsed; // 已是好友,无需再加
                }
                else if (ActionText.Length > 0)
                {
                    ActionTextVisibility = Visibility.Visible; // 群主/管理员标记 + 加好友按钮并存
                }
            }

            // 移出权限:群主可移任何人(除自己/群主);管理员可移普通成员(除自己/群主/其他管理员)。
            // 群主视角另有"设为/取消管理"按钮(仅群主可设管理员,服务器校验)
            if (!isMe && UserId != ownerId && (isOwnerRole || (isAdminRole && !isAdmin)))
            {
                if (isOwnerRole)
                {
                    AdminButtonText = isAdmin ? "取消管理" : "设为管理";
                    AdminButtonVisibility = Visibility.Visible;
                }
                KickButtonVisibility = Visibility.Visible;
                // 与"管理"标记同位(Column=2),有按钮时不再显示角色标记,防重叠
                ActionTextVisibility = Visibility.Collapsed;
            }
        }

        /// <summary>切换后本地翻转(服务器广播 groupUpdated 时会整体重建)。</summary>
        public void ToggleAdmin() => IsAdmin = !IsAdmin;
    }
}
