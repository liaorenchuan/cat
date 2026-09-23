using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QPet.Core;
using QPet.Ui;
using QPet.Wpf.Views;

namespace QPet.Wpf.Dialogs;

/// <summary>
/// 待处理申请列表,逐条同意 / 拒绝。三个 Tab:好友申请 + 群申请(我是群主/管理员)+ 群邀请(有人拉我进群)。
/// 数据源:SyncClient 最近一次 welcome 快照 + Received 事件实时增补 / Handled 移除。
/// </summary>
public partial class RequestsDialog : Window
{
    private readonly SyncClient _client;
    private List<ReqItem> _items = new();
    private List<GroupReqItem> _groupItems = new();
    private List<GroupInviteItem> _inviteItems = new();

    public RequestsDialog(SyncClient client, int tab = 0)
    {
        InitializeComponent();
        _client = client;
        Loaded += (_, _) =>
        {
            // 好友页通知只看好友申请,群聊页通知只看群申请(隐藏另一个页签)
            foreach (var item in Tabs.Items.OfType<TabItem>())
                item.Visibility = Tabs.Items.IndexOf(item) == tab ? Visibility.Visible : Visibility.Collapsed;
            Tabs.SelectedIndex = tab;

            _client.FriendRequestReceived += OnRequestReceived;
            _client.RequestHandled += OnRequestHandled;
            _client.GroupRequestReceived += OnGroupRequestReceived;
            _client.GroupRequestHandled += OnGroupRequestHandled;
            _client.GroupInviteReceived += OnGroupInviteReceived;
            _client.GroupInviteHandled += OnGroupInviteHandled;
            // 快照若在打开后才到(登录后立刻点开):welcome 到了整体刷一次
            _client.WelcomeReceived += OnWelcome;
            Refresh();
        };
        Closed += (_, _) =>
        {
            _client.FriendRequestReceived -= OnRequestReceived;
            _client.RequestHandled -= OnRequestHandled;
            _client.GroupRequestReceived -= OnGroupRequestReceived;
            _client.GroupRequestHandled -= OnGroupRequestHandled;
            _client.GroupInviteReceived -= OnGroupInviteReceived;
            _client.GroupInviteHandled -= OnGroupInviteHandled;
            _client.WelcomeReceived -= OnWelcome;
        };
    }

    private sealed class ReqItem
    {
        public FriendRequest R = null!;
        // WPF 绑定只认属性:显示名/留言/头像经属性暴露
        public string FromName => R.FromName;
        public string Text => R.Text;
        public string TimeHint => DateTimeOffset.FromUnixTimeMilliseconds(R.CreatedAtMs)
            .ToLocalTime().ToString("MM-dd HH:mm");
        // 头像与好友列表同款:首字 + 账号哈希固定配色
        public string AvatarChar => R.FromName.Length > 0 ? R.FromName.Substring(0, 1) : "?";
        public Brush AvatarBrush => ChatView.AvatarBrushFor(R.FromUserId);
    }

    private sealed class GroupReqItem
    {
        public GroupRequest R = null!;
        public string GroupName => R.GroupName;
        public string TimeHint => DateTimeOffset.FromUnixTimeMilliseconds(R.CreatedAtMs)
            .ToLocalTime().ToString("MM-dd HH:mm");
        public string RequestText => $"{R.FromName} 申请加入「{R.GroupName}」";
        // 群头像:👥 绿块,与群聊条目一致
        public string AvatarChar => "👥";
        public Brush AvatarBrush => UiPalette.GreenBg;
    }

    private sealed class GroupInviteItem
    {
        public GroupInvite R = null!;
        public string FromName => R.FromName;
        public string TimeHint => DateTimeOffset.FromUnixTimeMilliseconds(R.CreatedAtMs)
            .ToLocalTime().ToString("MM-dd HH:mm");
        public string RequestText => $"{R.FromName} 邀请你加入「{R.GroupName}」";
        // 发起者头像:首字 + 账号哈希固定配色(与好友申请一致)
        public string AvatarChar => R.FromName.Length > 0 ? R.FromName.Substring(0, 1) : "?";
        public Brush AvatarBrush => ChatView.AvatarBrushFor(R.FromUserId);
    }

    private void OnWelcome(AuthSnapshot snap) => Refresh();

    private void Refresh()
    {
        _items = _client.LastSnapshot.Requests.Select(r => new ReqItem { R = r }).ToList();
        ReqList.ItemsSource = _items;
        HintText.Text = _items.Count == 0 ? "没有待处理的好友申请" : $"{_items.Count} 条待处理申请";

        _groupItems = _client.LastSnapshot.GroupRequests.Select(r => new GroupReqItem { R = r }).ToList();
        GroupReqList.ItemsSource = _groupItems;
        GroupHintText.Text = _groupItems.Count == 0 ? "没有待审批的加群申请" : $"{_groupItems.Count} 条待审批申请";

        _inviteItems = _client.LastSnapshot.GroupInvites.Select(r => new GroupInviteItem { R = r }).ToList();
        InviteList.ItemsSource = _inviteItems;
        InviteHintText.Text = _inviteItems.Count == 0 ? "没有待同意的群邀请" : $"{_inviteItems.Count} 条待同意邀请";
    }

    // ---- 好友申请 ----

    /// <summary>我审批过的申请已回执(服务器推给审批人):从列表移除(与 Respond 本地移除幂等)。</summary>
    private void OnRequestHandled(long requestId)
    {
        if (_items.RemoveAll(x => x.R.ReqId == requestId) == 0)
            return;
        ReqList.ItemsSource = null;
        ReqList.ItemsSource = _items;
        HintText.Text = _items.Count == 0 ? "没有待处理的好友申请" : $"{_items.Count} 条待处理申请";
    }

    /// <summary>新申请实时增补(申请处理结果不推给被申请人,这里只管收新申请)。</summary>
    private void OnRequestReceived(FriendRequest req)
    {
        if (_items.Any(x => x.R.ReqId == req.ReqId))
            return;
        _items.Insert(0, new ReqItem { R = req });
        ReqList.ItemsSource = null;
        ReqList.ItemsSource = _items;
        HintText.Text = $"{_items.Count} 条待处理申请";
    }

    private void OnAccept(object sender, RoutedEventArgs e) => Respond(sender, true);

    private void OnReject(object sender, RoutedEventArgs e) => Respond(sender, false);

    private void Respond(object sender, bool accept)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ReqItem item })
            return;
        _client.RespondFriendRequest(item.R.ReqId, accept);
        // 同意时服务器推 FriendAdded 给双方(ChatView.OnFriendAdded 刷新好友列表);
        // handled 回执同到,本地先移除该项(幂等)
        _items.Remove(item);
        ReqList.ItemsSource = null;
        ReqList.ItemsSource = _items;
        HintText.Text = _items.Count == 0 ? "没有待处理的好友申请" : $"{_items.Count} 条待处理申请";
    }

    // ---- 群申请(群主/管理员审批) ----

    /// <summary>我审批的申请已被处理:从待审批列表移除。</summary>
    private void OnGroupRequestHandled(long requestId)
    {
        if (_groupItems.RemoveAll(x => x.R.ReqId == requestId) == 0)
            return;
        GroupReqList.ItemsSource = null;
        GroupReqList.ItemsSource = _groupItems;
        GroupHintText.Text = _groupItems.Count == 0 ? "没有待审批的加群申请" : $"{_groupItems.Count} 条待审批申请";
    }

    /// <summary>新加群申请实时增补(推给群主/管理员)。</summary>
    private void OnGroupRequestReceived(GroupRequest req)
    {
        if (_groupItems.Any(x => x.R.ReqId == req.ReqId))
            return;
        _groupItems.Insert(0, new GroupReqItem { R = req });
        GroupReqList.ItemsSource = null;
        GroupReqList.ItemsSource = _groupItems;
        GroupHintText.Text = $"{_groupItems.Count} 条待审批申请";
    }

    private void OnGroupAccept(object sender, RoutedEventArgs e) => GroupRespond(sender, true);

    private void OnGroupReject(object sender, RoutedEventArgs e) => GroupRespond(sender, false);

    private void GroupRespond(object sender, bool accept)
    {
        if (sender is not System.Windows.Controls.Button { Tag: GroupReqItem item })
            return;
        _client.RespondGroupRequest(item.R.ReqId, accept);
        // 同意后申请人由 groupUpdated 推送自动入群;本地先移除该项(审批成功即视为已处理)
        _groupItems.Remove(item);
        GroupReqList.ItemsSource = null;
        GroupReqList.ItemsSource = _groupItems;
        GroupHintText.Text = _groupItems.Count == 0 ? "没有待审批的加群申请" : $"{_groupItems.Count} 条待审批申请";
    }

    // ---- 群邀请(有人拉我进群,需我同意) ----

    /// <summary>我收到的邀请已被处理(拒绝/群解散):从待同意列表移除。</summary>
    private void OnGroupInviteHandled(long inviteId)
    {
        if (_inviteItems.RemoveAll(x => x.R.InviteId == inviteId) == 0)
            return;
        InviteList.ItemsSource = null;
        InviteList.ItemsSource = _inviteItems;
        InviteHintText.Text = _inviteItems.Count == 0 ? "没有待同意的群邀请" : $"{_inviteItems.Count} 条待同意邀请";
    }

    /// <summary>新群邀请实时增补。</summary>
    private void OnGroupInviteReceived(GroupInvite invite)
    {
        if (_inviteItems.Any(x => x.R.InviteId == invite.InviteId))
            return;
        _inviteItems.Insert(0, new GroupInviteItem { R = invite });
        InviteList.ItemsSource = null;
        InviteList.ItemsSource = _inviteItems;
        InviteHintText.Text = $"{_inviteItems.Count} 条待同意邀请";
    }

    private void OnInviteAccept(object sender, RoutedEventArgs e) => InviteRespond(sender, true);

    private void OnInviteReject(object sender, RoutedEventArgs e) => InviteRespond(sender, false);

    private void InviteRespond(object sender, bool accept)
    {
        if (sender is not System.Windows.Controls.Button { Tag: GroupInviteItem item })
            return;
        _client.RespondGroupInvite(item.R.InviteId, accept);
        // 同意后入群由 groupUpdated 推送自动刷新;本地先移除该项(已处理)
        _inviteItems.Remove(item);
        InviteList.ItemsSource = null;
        InviteList.ItemsSource = _inviteItems;
        InviteHintText.Text = _inviteItems.Count == 0 ? "没有待同意的群邀请" : $"{_inviteItems.Count} 条待同意邀请";
    }
}
