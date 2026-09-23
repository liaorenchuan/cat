using System.Windows;
using System.Windows.Controls;
using QPet.Admin.Services;
using QPet.Ui;
using QPet.Ui.Dialogs;

namespace QPet.Admin;

/// <summary>
/// 账号详情页:昵称/账号只读展示 + 密码可改 + 好友列表(解除好友) + 群聊列表(移出群聊)。
/// 加好友/加群改为候选点选:输入账号或昵称(群名或群号)实时搜索,候选显示在输入框下方,点 ＋ 直接建立。
/// 关系操作经服务器推事件,在线客户端即时刷新。
/// </summary>
public partial class AccountDetailDialog : AdminDialogBase
{
    private readonly string _userId;
    private readonly string _account; // 当前账号(创建群时作群主)
    private List<FriendRow> _allFriends = new();       // 当前好友(候选排除"已是好友")
    private List<GroupRow> _allGroups = new();         // 当前群聊(候选排除"已加入")
    private List<UserCandidate> _allUsers = new();     // 全部用户(加好友候选池)
    private List<GroupCandidate> _allGroupsPool = new(); // 全部群(加群候选池)

    private sealed class FriendRow : RowViewBase
    {
        public string UserId { get; set; } = "";
        public string Account { get; set; } = ""; // 对方账号(展示账号,不显示内部 userId)
        public string AccountShort => Account.Length > 14 ? Account[..14] + "…" : Account;
    }

    private sealed class GroupRow : GroupRowViewBase
    {
        public string NumText => Num.Length > 0 ? $"群号 {Num}" : IsPublic ? "公共群聊" : $"ID {GroupId}";
        public string KindText => IsPublic ? "公共群" : "普通群";
        public string MemberCountText => $"{MemberCount} 人";
    }

    /// <summary>加好友候选(昵称/账号/在线状态)。</summary>
    private sealed class UserCandidate : RowViewBase
    {
        public string UserId { get; set; } = "";
        public string Account { get; set; } = "";
        public string Display => Nickname.Length > 0 ? $"{Nickname} ({Account})" : Account;
    }

    /// <summary>加群候选(群名/群号/类型/人数)。</summary>
    private sealed class GroupCandidate : GroupRowViewBase
    {
        public string NameText => IsPublic ? $"🌍 {Name}" : $"👥 {Name}";
        public string MetaText => $"群号 {Num} · {(IsPublic ? "公共群" : "好友群")} · {MemberCount} 人";
    }

    public AccountDetailDialog(AdminApiClient api,
        string userId, string account, string nickname, string password, bool isBanned, bool online)
        : base(api)
    {
        InitializeComponent();
        _userId = userId;
        _account = account;

        // 头部:昵称 + 账号 + 状态徽章
        TitleText.Text = nickname.Length > 0 ? nickname : account;
        AvatarText.Text = account.Length > 0 ? account.Substring(0, 1) : "?";
        AccountText.Text = account;
        NicknameText.Text = nickname.Length > 0 ? nickname : "(未设置)";
        PasswordBox.Text = password; // 明文展示(老账号无明文则显示空,可重设)

        StatusBadge.Text = isBanned ? "已禁用" : online ? "在线" : "离线";
        StatusBadgeBorder.Background = isBanned
            ? UiPalette.Error
            : online
                ? UiPalette.Success
                : UiPalette.Brown;

        _ = LoadRelationsAsync();
        _ = LoadCandidatesAsync();
    }

    protected override TextBlock MsgBox => ErrText;

    // ---- 关系列表 ----

    private async Task LoadRelationsAsync()
    {
        try
        {
            using var r = await Api.GetAsync($"relations?userId={_userId}");
            if (r.Unauthorized)
            {
                ShowErr("登录已过期,请重新登录管理端");
                return;
            }
            if (!r.IsSuccess)
            {
                ShowErr($"加载关系失败 ({r.Status})");
                return;
            }

            var friends = new List<FriendRow>();
            var groups = new List<GroupRow>();
            using var doc = r.Doc!;
            foreach (var item in doc.RootElement.GetProperty("friends").EnumerateArray())
            {
                friends.Add(new FriendRow
                {
                    UserId = item.GetProperty("userId").GetString() ?? "",
                    Account = item.GetProperty("account").GetString() ?? "",
                    Nickname = item.GetProperty("nickname").GetString() ?? "",
                    Online = item.GetProperty("online").GetBoolean(),
                });
            }
            foreach (var item in doc.RootElement.GetProperty("groups").EnumerateArray())
            {
                groups.Add(new GroupRow
                {
                    GroupId = item.GetProperty("groupId").GetString() ?? "",
                    Num = item.TryGetProperty("num", out var n) ? n.GetString() ?? "" : "",
                    Name = item.GetProperty("name").GetString() ?? "",
                    IsPublic = item.GetProperty("isPublic").GetBoolean(),
                    MemberCount = item.GetProperty("members").GetArrayLength(),
                });
            }

            _allFriends = friends;
            _allGroups = groups;
            FriendList.ItemsSource = friends;
            GroupList.ItemsSource = groups;
            FriendEmptyText.Text = friends.Count == 0 ? "还没有好友" : "";
            GroupEmptyText.Text = groups.Count == 0 ? "还没有群聊" : "";
            RefreshFriendCandidates();
            RefreshGroupCandidates();
        }
        catch (Exception ex)
        {
            ShowErr("加载失败: " + ex.Message);
        }
    }

    /// <summary>加载候选池:全部用户(GET /api/admin/users)+ 全部群(GET /api/admin/groups)。</summary>
    private async Task LoadCandidatesAsync()
    {
        try
        {
            using (var r = await Api.GetAsync("users"))
            {
                if (r.IsSuccess)
                {
                    using var doc = r.Doc!;
                    var users = new List<UserCandidate>();
                    foreach (var item in doc.RootElement.GetProperty("users").EnumerateArray())
                    {
                        users.Add(new UserCandidate
                        {
                            UserId = item.GetProperty("userId").GetString() ?? "",
                            Account = item.GetProperty("account").GetString() ?? "",
                            Nickname = item.GetProperty("nickname").GetString() ?? "",
                            Online = item.GetProperty("online").GetBoolean(),
                        });
                    }
                    _allUsers = users;
                }
            }
            using (var r = await Api.GetAsync("groups"))
            {
                if (r.IsSuccess)
                {
                    using var doc = r.Doc!;
                    var groups = new List<GroupCandidate>();
                    foreach (var item in doc.RootElement.GetProperty("groups").EnumerateArray())
                    {
                        groups.Add(new GroupCandidate
                        {
                            GroupId = item.GetProperty("groupId").GetString() ?? "",
                            Num = item.GetProperty("num").GetString() ?? "",
                            Name = item.GetProperty("name").GetString() ?? "",
                            IsPublic = item.GetProperty("isPublic").GetBoolean(),
                            MemberCount = item.GetProperty("memberCount").GetInt32(),
                        });
                    }
                    _allGroupsPool = groups;
                }
            }
            RefreshFriendCandidates();
            RefreshGroupCandidates();
        }
        catch (Exception ex)
        {
            ShowErr("加载候选失败: " + ex.Message);
        }
    }

    // ---- 候选搜索(输入即搜,显示在输入框下方) ----

    private void OnFriendSearchChanged(object sender, TextChangedEventArgs e) => RefreshFriendCandidates();

    /// <summary>按账号/昵称过滤候选池,排除自己和已是好友;结果在输入框下方显示。</summary>
    private void RefreshFriendCandidates()
    {
        var kw = FriendSearchBox.Text.Trim();
        if (kw.Length == 0)
        {
            FriendCandidates.Visibility = Visibility.Collapsed;
            return;
        }
        var list = _allUsers
            .Where(u => u.UserId != _userId && !_allFriends.Any(f => f.UserId == u.UserId))
            .Where(u => u.Account.Contains(kw, StringComparison.OrdinalIgnoreCase)
                        || u.Nickname.Contains(kw, StringComparison.OrdinalIgnoreCase))
            .Take(30)
            .ToList();
        FriendCandidates.ItemsSource = list;
        FriendCandidates.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnGroupSearchChanged(object sender, TextChangedEventArgs e) => RefreshGroupCandidates();

    /// <summary>按群名/群号过滤候选池,排除已加入的群;结果在输入框下方显示。</summary>
    private void RefreshGroupCandidates()
    {
        var kw = GroupSearchBox.Text.Trim();
        if (kw.Length == 0)
        {
            GroupCandidates.Visibility = Visibility.Collapsed;
            return;
        }
        var list = _allGroupsPool
            .Where(g => !_allGroups.Any(x => x.GroupId == g.GroupId))
            .Where(g => g.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)
                        || g.Num.Contains(kw, StringComparison.OrdinalIgnoreCase))
            .Take(30)
            .ToList();
        GroupCandidates.ItemsSource = list;
        GroupCandidates.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>点候选加好友:直接建立好友关系(跳过申请审批,在线客户端即时刷新)。</summary>
    private async void OnFriendCandidateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: UserCandidate c })
            return;
        if (!await PostAsync("addFriend", new { userId = _userId, targetAccount = c.Account }))
            return;
        ShowSuccess($"已添加好友 {c.Display} ✓");
        await LoadRelationsAsync(); // 好友列表刷新,该候选自动从候选里排除
    }

    /// <summary>点候选加群:直接加入(跳过申请审批,在线成员即时刷新)。</summary>
    private async void OnGroupCandidateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GroupCandidate c })
            return;
        if (!await PostAsync("addToGroup", new { userId = _userId, groupNum = c.Num }))
            return;
        ShowSuccess($"已加入群聊「{c.Name}」✓");
        await LoadRelationsAsync(); // 群聊列表刷新,该候选自动从候选里排除
    }

    // ---- 密码修改 ----

    private async void OnSavePassword(object sender, RoutedEventArgs e)
    {
        var newPassword = PasswordBox.Text.Trim();
        if (newPassword.Length < 4)
        {
            ShowErr("密码至少 4 位");
            return;
        }
        try
        {
            using var r = await Api.PostAsync("resetPassword", new { userId = _userId, newPassword });
            if (r.Unauthorized)
            {
                ShowErr("登录已过期,请重新登录管理端");
                return;
            }
            if (!r.IsSuccess)
            {
                ShowErr($"保存失败 ({r.Status}): {r.Error}");
                return;
            }
            ShowSuccess("密码已更新 ✓");
        }
        catch (Exception ex)
        {
            ShowErr("请求失败: " + ex.Message);
        }
    }

    // ---- 好友 / 群聊操作 ----

    private void OnFriendSelected(object sender, SelectionChangedEventArgs e) =>
        UnfriendButton.IsEnabled = FriendList.SelectedItem is FriendRow;

    private void OnGroupSelected(object sender, SelectionChangedEventArgs e) =>
        KickButton.IsEnabled = GroupList.SelectedItem is GroupRow;

    /// <summary>解除好友关系(双方好友列表都移除;在线客户端即时刷新)。</summary>
    private async void OnUnfriend(object sender, RoutedEventArgs e)
    {
        if (FriendList.SelectedItem is not FriendRow f)
            return;
        if (new ConfirmDialog("解除好友",
                $"解除好友关系「{f.Nickname}」?\n双方好友列表都会移除(在线客户端即时刷新),聊天记录保留。") { Owner = this }.ShowDialog() != true)
            return;
        if (!await PostAsync("unfriend", new { userId = _userId, friendUserId = f.UserId }))
            return;
        await LoadRelationsAsync();
    }

    /// <summary>把该用户移出群聊(剩余成员即时刷新)。</summary>
    private async void OnKick(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupRow g)
            return;
        if (new ConfirmDialog("移出群聊", $"把该用户移出群聊「{g.Name}」?") { Owner = this }.ShowDialog() != true)
            return;
        if (!await PostAsync("kickGroup", new { userId = _userId, groupId = g.GroupId }))
            return;
        await LoadRelationsAsync();
    }

    /// <summary>创建群:群主 = 当前账号,弹窗输入群名并勾选成员(全部用户,无需好友关系)。</summary>
    private void OnCreateGroup(object sender, RoutedEventArgs e)
    {
        var dlg = new CreateGroupDialog(Api, _account)
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true)
            return;
        ShowSuccess("群创建成功 ✓");
        _ = LoadRelationsAsync();
        _ = LoadCandidatesAsync(); // 候选池出现新群
    }
}
