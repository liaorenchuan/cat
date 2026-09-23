using System.Windows;
using System.Windows.Controls;
using QPet.Admin.Services;
using QPet.Ui;
using QPet.Ui.Dialogs;

namespace QPet.Admin;

/// <summary>
/// 群聊管理:全部群列表(群号/群名/类型/人数)→ 选中群查看成员 →
/// 移出成员(kickGroup,不足 2 人服务器自动解散)/ 添加成员(全用户勾选)/ 解散群聊(公共群不可解散)。
/// 所有变更经服务器推事件,在线客户端即时刷新。
/// </summary>
public partial class GroupManageDialog : AdminDialogBase
{
    private List<GroupRow> _groups = new();

    private sealed class GroupRow : GroupRowViewBase
    {
        public string OwnerId { get; set; } = "";
        public List<MemberRow> Members { get; set; } = new();
        public string TitleText => IsPublic ? $"🌍 {Name}" : $"👥 {Name}";
        public string SubText => IsPublic ? $"公共群 · 群号 {Num}" : $"好友群 · 群号 {Num}";
        public string CountText => $"{Members.Count} 人";
    }

    private sealed class MemberRow
    {
        public string UserId { get; set; } = "";
        public string Nickname { get; set; } = "";
        public string Account { get; set; } = "";
        public bool IsOwner { get; set; }
        public bool IsAdmin { get; set; }
        public string Display => Nickname.Length > 0 ? $"{Nickname} ({Account})" : Account;
        public Visibility OwnerBadgeVisibility => IsOwner ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AdminBadgeVisibility => IsAdmin && !IsOwner ? Visibility.Visible : Visibility.Collapsed;
        /// <summary>排序权重:群主 0 > 管理员 1 > 普通成员 2。</summary>
        public int Rank => IsOwner ? 0 : IsAdmin ? 1 : 2;
    }

    public GroupManageDialog(AdminApiClient api) : base(api)
    {
        InitializeComponent();
        _ = LoadGroupsAsync();
    }

    protected override TextBlock MsgBox => ErrText;

    /// <summary>加载全部群(含成员明细,GET /api/admin/groups)。</summary>
    private async Task LoadGroupsAsync()
    {
        try
        {
            using var r = await Api.GetAsync("groups");
            if (r.Unauthorized)
            {
                ShowErr("登录已过期,请重新登录管理端");
                return;
            }
            if (!r.IsSuccess)
            {
                ShowErr($"加载群聊失败 ({r.Status})");
                return;
            }

            var groups = new List<GroupRow>();
            using var doc = r.Doc!;
            foreach (var item in doc.RootElement.GetProperty("groups").EnumerateArray())
            {
                var g = new GroupRow
                {
                    GroupId = item.GetProperty("groupId").GetString() ?? "",
                    Num = item.GetProperty("num").GetString() ?? "",
                    Name = item.GetProperty("name").GetString() ?? "",
                    IsPublic = item.GetProperty("isPublic").GetBoolean(),
                    OwnerId = item.GetProperty("ownerId").GetString() ?? "",
                };
                foreach (var m in item.GetProperty("members").EnumerateArray())
                {
                    g.Members.Add(new MemberRow
                    {
                        UserId = m.GetProperty("userId").GetString() ?? "",
                        Nickname = m.GetProperty("nickname").GetString() ?? "",
                        Account = m.GetProperty("account").GetString() ?? "",
                        IsOwner = g.OwnerId == (m.GetProperty("userId").GetString() ?? ""),
                        IsAdmin = m.TryGetProperty("isAdmin", out var a) && a.GetBoolean(),
                    });
                }
                g.MemberCount = g.Members.Count;
                // 排序:群主置顶,然后是群管理员,再是普通成员(同权重保持原顺序)
                g.Members.Sort((x, y) => x.Rank - y.Rank);
                groups.Add(g);
            }
            _groups = groups;
            GroupCountText.Text = $"共 {groups.Count} 个群聊";
            // 刷新后尽量保持原选中群
            var keepId = (GroupList.SelectedItem as GroupRow)?.GroupId ?? "";
            GroupList.ItemsSource = groups;
            if (keepId.Length > 0)
            {
                var keep = groups.FirstOrDefault(g => g.GroupId == keepId);
                if (keep is not null)
                    GroupList.SelectedItem = keep;
            }
        }
        catch (Exception ex)
        {
            ShowErr("加载失败: " + ex.Message);
        }
    }

    private void OnGroupSelected(object sender, SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupRow g)
        {
            RenameButton.IsEnabled = false;
            AddMemberButton.IsEnabled = false;
            DisbandButton.IsEnabled = false;
            return;
        }
        GroupNameText.Text = g.TitleText;
        KindBadge.Background = g.KindBrush;
        KindText.Text = g.IsPublic ? "公共群" : "好友群";
        GroupMetaText.Text = $"群号 {g.Num} · {g.Members.Count} 人 · 群主: {(g.Members.FirstOrDefault(m => m.IsOwner)?.Display ?? "未知")}";
        MemberList.ItemsSource = g.Members;
        RenameButton.IsEnabled = !g.IsPublic; // 公共群全员共享,不可改名
        AddMemberButton.IsEnabled = true;
        DisbandButton.IsEnabled = !g.IsPublic; // 公共群全员共享,不可解散
        ErrText.Visibility = Visibility.Collapsed;
    }

    /// <summary>改群名(公共群按钮已禁用;改名后 GroupUpdated 推送,在线客户端即时刷新)。</summary>
    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupRow g)
            return;
        var dlg = new RenameGroupDialog(g.Name) { Owner = this };
        if (dlg.ShowDialog() != true)
            return;
        if (!await PostAsync("renameGroup", new { groupId = g.GroupId, name = dlg.NewName }))
            return;
        ShowSuccess($"群名已改为「{dlg.NewName}」✓");
        await LoadGroupsAsync(); // 群名/列表刷新
    }

    /// <summary>移出成员(不足 2 人服务器自动解散,群从列表消失)。</summary>
    private async void OnRemoveMember(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MemberRow m })
            return;
        if (GroupList.SelectedItem is not GroupRow g)
            return;
        if (new ConfirmDialog("移出群聊", $"把 {m.Display} 移出群聊「{g.Name}」?") { Owner = this }.ShowDialog() != true)
            return;
        if (!await PostAsync("kickGroup", new { userId = m.UserId, groupId = g.GroupId }))
            return;
        ShowSuccess($"已移出 {m.Display}");
        await LoadGroupsAsync(); // 若群被自动解散,列表同步消失
    }

    /// <summary>添加成员:弹出全用户勾选(排除已在群的),逐账号直接加群。</summary>
    private void OnAddMembers(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupRow g)
            return;
        var dlg = new AddGroupMembersDialog(Api, g.Num, g.Name, g.Members.Select(m => m.UserId))
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true)
            return;
        _ = LoadGroupsAsync(); // 成员数变化(对话框内已逐个调用,这里同步一次列表)
    }

    /// <summary>解散群聊(公共群按钮已禁用;全部成员在线即时移除)。</summary>
    private async void OnDisband(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupRow g)
            return;
        if (new ConfirmDialog("解散群聊",
                $"解散群聊「{g.Name}」(群号 {g.Num})?\n聊天记录将全部删除,所有成员立即退出。") { Owner = this }.ShowDialog() != true)
            return;
        if (!await PostAsync("disbandGroup", new { groupId = g.GroupId }))
            return;
        ShowSuccess("群聊已解散 ✓");
        await LoadGroupsAsync();
    }
}
