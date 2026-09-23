using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QPet.Core;
using QPet.Ui;

namespace QPet.Wpf.Views;

/// <summary>
/// 主界面(登录后):顶栏(昵称+账号,点击弹个人主页) + 四个页签(消息/好友/群聊/宠物)。
/// 消息页 = 会话列表(好友+群合并,点开进聊天窗);好友页 = 搜索+申请通知+好友列表(点开进详情);
/// 群聊页 = 创建+搜索+群申请+群列表(点开进详情);宠物页 = 内嵌宠物小屋。
/// 纯客户端:经 SyncClient 连局域网服务器,事件驱动刷新(事件已在 UI 线程,处理函数直接刷控件)。
/// 视图缓存复用:Loaded 订阅 / Unloaded 退订,避免切走时重复渲染或计时器空转。
/// </summary>
public partial class ChatView : UserControl
{
    private readonly App _app;
    private readonly SyncClient _client;
    private string _me; // OnLoaded 刷新:视图缓存实例,换账号登录后不能沿用旧 ID

    // 头像色板(会话列表/好友列表/消息气泡共用):同一账号哈希固定配色,一眼识别
    // (首色与 SurfacePressed 同值取色板;其余为头像专用色,保留原值)
    private static readonly Brush[] AvatarPalette =
    {
        UiPalette.SurfacePressed,
        new SolidColorBrush(Color.FromRgb(0xE4, 0xEF, 0xFF)),
        new SolidColorBrush(Color.FromRgb(0xE5, 0xF6, 0xE0)),
        new SolidColorBrush(Color.FromRgb(0xFF, 0xE4, 0xEE)),
        new SolidColorBrush(Color.FromRgb(0xEA, 0xE5, 0xFF)),
        new SolidColorBrush(Color.FromRgb(0xFF, 0xF0, 0xD4)),
    };

    /// <summary>群聊头像底色(浅绿,与群聊页一致)。</summary>
    private static readonly Brush GroupAvatarBrush = UiPalette.GreenBg;

    /// <summary>按账号取固定头像色(哈希位与运算防 int.MinValue 取反崩溃)。</summary>
    internal static Brush AvatarBrushFor(string userId) =>
        AvatarPalette[(userId.GetHashCode() & 0x7FFFFFFF) % AvatarPalette.Length];

    // 好友/群:本地维护列表,welcome 全量重建,事件增量更新(服务器不做全量重推)
    private readonly List<FriendInfo> _friends = new();
    private readonly List<GroupInfo> _groups = new();

    // 消息:全量可见(增量追加,Seq 去重) + 当前会话视图(含日期栏,按需 AppendToView)
    private readonly List<ChatMessage> _all = new();
    private readonly HashSet<long> _seen = new(); // 防全量历史与实时推送重叠
    private readonly List<object> _view = new();
    private readonly List<ConvEntry> _convs = new();
    private readonly List<FriendItem> _friendItems = new();
    private readonly List<GroupItem> _groupItems = new();

    // 未读状态(客户端本地,不依赖服务器):key = "{convType}:{convId}"
    private readonly Dictionary<string, int> _unread = new();
    private readonly Dictionary<string, ChatMessage> _lastPreview = new();
    private readonly Dictionary<string, long> _convMaxSeq = new(); // 各会话最大 Seq(SelectConv 清零用)
    private readonly HashSet<string> _hiddenConvs = new(); // 已删除会话(左侧点✕移除;该会话再来新消息自动重新出现)

    // 已读回执(服务器 conv_reads 落库 + read 帧推送):key = "{convType}:{convId}",
    // 内层 = readerUserId → 已读到的 Seq(我的消息 ≤ 该 Seq = 被对方已读)
    private readonly Dictionary<string, Dictionary<string, long>> _convReadSeq = new();

    /// <summary>客户端消息缓存上限(与服务器 MaxMessages 对齐,防长期挂机内存膨胀)。</summary>
    private const int MaxMessages = 5000;

    private int _convType;          // 未打开会话时为空("" 不匹配任何消息,右侧保持空白)
    private string _convId = "";
    private bool _convOpen;          // 是否已打开某个会话(登录/关闭会话后为 false,右侧空白)
    private bool _loaded;
    private bool _wasOffline;        // 上次加载时的离线状态(在线↔离线切换需清缓存)
    private bool _convInitialized;   // 首次加载时默认进公共群,之后保持用户当前会话
    private bool _convListRefreshing; // RefreshConvList 重建期间:恢复选中触发的 SelectionChanged 重入忽略
    private bool _historyLoaded;     // 全量历史只拉一次(换账号会重置),避免每次切回重复拉
    private bool _historyDone;       // 历史补拉完成:期间消息只累积不刷新,完成时一次性重建(防批量卡顿)
    private ChatMessage? _viewLastMsg; // 当前视图最后一条消息(O(1) 判断时间分组,替代逐条反向扫描)
    private int _pendingFriendCount; // 待我处理的好友申请(好友页通知徽章)
    private int _pendingGroupCount;  // 待我审批的加群申请(群聊页通知徽章)
    private int _pendingInviteCount; // 待我同意的群邀请(群聊页通知徽章)
    private string _profileTargetId = "";  // 用户主页弹出时点"加好友"的目标
    private string _profilePendingId = ""; // 正在等待服务器返回资料的用户

    public ChatView(App app)
    {
        InitializeComponent();
        _app = app;
        _client = App.SyncClient;
        _me = app.MyUserId;
        // 填充 emoji 面板(每个 emoji 一个可点 TextBlock)
        foreach (var emoji in Emojis)
        {
            var tb = new TextBlock
            {
                Text = emoji,
                FontSize = 16,
                Margin = new Thickness(3),
                Cursor = Cursors.Hand,
            };
            tb.MouseLeftButtonUp += OnEmojiClick;
            EmojiWrap.Children.Add(tb);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;

        // 换账号或 在线↔离线切换:旧账号的消息/游标缓存必须清空,否则串号或跳过历史拉取
        var prevMe = _me;
        _me = _app.MyUserId; // 缓存视图每次重新显示时刷新身份(退出登录换账号)
        var offline = _client.IsOffline;
        if (offline != _wasOffline || _me != prevMe)
        {
            _all.Clear();
            _seen.Clear();
            _view.Clear();
            _viewLastMsg = null;
            _historyLoaded = false; // 新账号/新模式需要重新拉自己的历史(离线注入缓存后置真)
            _historyDone = false;
            _unread.Clear();
            _lastPreview.Clear();
            _convMaxSeq.Clear();
            CloseConv(); // 新账号不自动打开旧会话(右侧空白)
        }
        _wasOffline = offline;
        // 个人头部
        MyNickText.Text = _app.MyNickname;
        MyAccountText.Text = _app.MyAccount;
        AvatarText.Text = _app.MyNickname.Length > 0 ? _app.MyNickname.Substring(0, 1) : "?";
        UpdateMyStatusDot(); // 资料卡状态点:离线模式置灰

        if (offline)
        {
            // 离线模式:不订阅事件、不拉历史(服务器事件不会触发),直接从本地缓存初始化
            ApplySnapshot(_client.LastSnapshot);
            RefreshRequestBadge();
            ApplyCachedMessages(_app.CurrentCache);
            ShowOfflineBanner(true, "离线模式 · 未连接服务器,仅显示本地缓存聊天记录,消息不可发送");
            if (!_convInitialized)
            {
                _convInitialized = true;
                ShowTab(0);
                CloseConv();
            }
            else
            {
                ShowTab(_currentTab);
            }
            return;
        }

        ShowOfflineBanner(false, ""); // 在线:隐藏离线横幅(离线登录残留需清)

        // 订阅 SyncClient 事件(均在 UI 线程)
        _client.WelcomeReceived += OnWelcome;
        _client.ChatReceived += OnMessage;
        _client.ReadReceived += OnRead;
        _client.ReadStatesReceived += OnReadStates;
        _client.UsersReceived += OnUsersChanged;
        _client.FriendRequestReceived += OnRequestReceived;
        _client.FriendRequestResultReceived += OnFriendRequestResultReceived;
        _client.RequestHandled += OnRequestHandled;
        _client.FriendAdded += OnFriendAdded;
        _client.FriendDeleted += OnFriendDeleted;
        _client.GroupUpdated += OnGroupUpdated;
        _client.GroupRemoved += OnGroupRemoved;
        _client.GroupRequestReceived += OnGroupRequestReceived;
        _client.GroupRequestResultReceived += OnGroupRequestResultReceived;
        _client.GroupRequestHandled += OnGroupRequestHandled;
        _client.GroupInviteReceived += OnGroupInviteReceived;
        _client.GroupInviteResultReceived += OnGroupInviteResultReceived;
        _client.GroupInviteHandled += OnGroupInviteHandled;
        _client.ProfileUpdated += OnProfileUpdated;
        _client.UserProfileReceived += OnUserProfileReceived;
        _client.HistoryLoaded += OnHistoryLoaded;
        _client.SettingReceived += OnSettingUpdated;
        _client.UnreadUpdated += OnUnreadUpdated;
        _client.ErrorReceived += OnError;

        // 数据:welcome 快照可能已到(AuthOk 之后)或未到,两处都兜底
        ApplySnapshot(_client.LastSnapshot);
        RefreshRequestBadge();

        // 历史消息全量补拉(0 起)只在首次进入时做,避免来回切页签重复拉全量
        if (!_historyLoaded)
        {
            _historyLoaded = true;
            _historyDone = false;
            _ = _client.LoadHistoryAsync();
        }

        // 首次进入停在消息页签,但不自动打开任何会话(右侧空白,点左侧会话才打开);
        // 之后保持用户当前页签/会话
        if (!_convInitialized)
        {
            _convInitialized = true;
            ShowTab(0);
            CloseConv();
        }
        else
        {
            ShowTab(_currentTab);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 切走时全量退订,回来时 OnLoaded 重新订阅
        _client.WelcomeReceived -= OnWelcome;
        _client.ChatReceived -= OnMessage;
        _client.ReadReceived -= OnRead;
        _client.ReadStatesReceived -= OnReadStates;
        _client.UsersReceived -= OnUsersChanged;
        _client.FriendRequestReceived -= OnRequestReceived;
        _client.FriendRequestResultReceived -= OnFriendRequestResultReceived;
        _client.RequestHandled -= OnRequestHandled;
        _client.FriendAdded -= OnFriendAdded;
        _client.FriendDeleted -= OnFriendDeleted;
        _client.GroupUpdated -= OnGroupUpdated;
        _client.GroupRemoved -= OnGroupRemoved;
        _client.GroupRequestReceived -= OnGroupRequestReceived;
        _client.GroupRequestResultReceived -= OnGroupRequestResultReceived;
        _client.GroupRequestHandled -= OnGroupRequestHandled;
        _client.GroupInviteReceived -= OnGroupInviteReceived;
        _client.GroupInviteResultReceived -= OnGroupInviteResultReceived;
        _client.GroupInviteHandled -= OnGroupInviteHandled;
        _client.ProfileUpdated -= OnProfileUpdated;
        _client.UserProfileReceived -= OnUserProfileReceived;
        _client.HistoryLoaded -= OnHistoryLoaded;
        _client.ErrorReceived -= OnError;
        _client.SettingReceived -= OnSettingUpdated;
        _client.UnreadUpdated -= OnUnreadUpdated;
        _loaded = false; // 重新显示时 OnLoaded 重新订阅(否则消息/用户事件永久丢失)
    }

    // ---- 数据模型 ----

    /// <summary>消息页会话条目(好友+群合并,按最后消息时间倒序)。</summary>
    private sealed class ConvEntry
    {
        // 注意:WPF 绑定只认属性,字段会静默失败(列表显示空白)
        public int ConvType { get; set; }            // 0=好友 1=群
        public string ConvId { get; set; } = "";
        public string Title { get; set; } = "";      // 好友=备注优先;群=群备注优先
        public string Preview { get; set; } = "";    // 最后一条消息预览
        public string TimeText { get; set; } = "";   // 最后消息时间:今天 HH:mm / 昨天 / MM-dd
        public int Unread { get; set; }
        public long LastTicks { get; set; }          // 排序用(无消息 = 0 排最后,不绑定 UI)
        public bool Online { get; set; }             // 好友在线状态(离线圆点)
        public string AvatarText { get; set; } = ""; // 头像内容:群=👥,好友=昵称首字
        public Brush AvatarBrush { get; set; } = Brushes.Transparent; // 头像底色:群=浅绿,好友=固定配色
        public string UnreadText => Unread > 99 ? "99+" : Unread.ToString();
        public Visibility UnreadVisibility => Unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DotVisibility => ConvType == 0 ? Visibility.Visible : Visibility.Collapsed;
        public Brush DotBrush => Online ? UiPalette.Green : Brushes.Silver;
        public override string ToString() => Title;
    }

    /// <summary>好友页条目(点击进好友详情)。</summary>
    private sealed class FriendItem
    {
        public string UserId { get; set; } = "";
        public string Title { get; set; } = "";      // 备注优先
        public string AvatarChar { get; set; } = "";
        public Brush AvatarBrush { get; set; } = Brushes.Transparent; // 与会话列表同款配色
        public bool Online { get; set; }
        public string Sub { get; set; } = "";        // 昵称(备注存在时显示原昵称)
        public Brush OnlineBrush => Online ? UiPalette.Green : Brushes.Silver;
        public string StatusText => Online ? "在线" : "离线";
        public override string ToString() => Title;
    }

    /// <summary>群聊页条目(点击进群详情)。</summary>
    private sealed class GroupItem
    {
        public string GroupId { get; set; } = "";
        public string Title { get; set; } = "";      // 群备注优先
        public string Sub { get; set; } = "";        // 成员数
        public override string ToString() => Title;
    }

    private sealed class MsgItem
    {
        // Msg 必须是属性:{Binding Msg.Text} 绑定路径不经过字段
        public ChatMessage Msg { get; set; } = null!;
        public bool IsMine { get; set; }
        // 昵称:气泡内上方——私聊/群聊、自己/对方都显示(FromName 对我是我的昵称)
        public string ShowName => Msg.FromName;
        public Visibility NameVisibility => string.IsNullOrEmpty(ShowName) ? Visibility.Collapsed : Visibility.Visible;
        // 时间:气泡内下方,每条消息都显示 HH:mm
        public string TimeText => Msg.Time.ToLocalTime().ToString("HH:mm");
        // 已读标记:气泡左边,由 UpdateReadMarkers 维护(只标我最后一条已读消息,未读不显示)
        public bool ShowRead { get; set; }
        public string ReadText { get; set; } = "";
        public Visibility ReadVisibility => ShowRead ? Visibility.Visible : Visibility.Collapsed;
        // 系统消息:整行居中灰字,隐藏头像/气泡
        public bool IsSystem => Msg.Type == 1;
        public Visibility SystemRowVisibility => IsSystem ? Visibility.Visible : Visibility.Collapsed;
        public Visibility BubbleRowVisibility => IsSystem ? Visibility.Collapsed : Visibility.Visible;
        // 头像:每条消息都有,自己的在右,对方的在左(点击查看主页/加好友)
        public string AvatarChar => Msg.FromName.Length > 0 ? Msg.FromName.Substring(0, 1) : "?";
        public Visibility ShowLeftAvatar => IsMine ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ShowRightAvatar => IsMine ? Visibility.Visible : Visibility.Collapsed;
        public HorizontalAlignment Align => IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        public Brush BubbleBrush => IsMine ? MyBubble : WhiteBubble;
        public Brush TextBrush => TextDark; // 浅色气泡上深色文字(自己的消息也是)
        public Thickness BubbleBorder => IsMine ? new Thickness(0) : new Thickness(1);
        // 同一发送者固定同色,一眼区分说话人(色板在 ChatView,列表/气泡共用)
        public Brush AvatarBrush => ChatView.AvatarBrushFor(Msg.FromUserId);
        // 在线状态点:我的点(连接时绿/离线灰)与对方点(仅私聊,按好友在线);群聊对方点隐藏
        public Brush MyDotBrush { get; set; } = Brushes.Silver;
        public Brush PeerDotBrush { get; set; } = Brushes.Silver;
        public bool ShowPeerDot { get; set; }
        public Visibility PeerDotVisibility => ShowPeerDot ? Visibility.Visible : Visibility.Collapsed;

        private static readonly Brush MyBubble = UiPalette.MyBubble; // 浅杏橙
        private static readonly Brush WhiteBubble = Brushes.White;
        private static readonly Brush TextDark = UiPalette.BubbleText;
    }

    // ---- 显示名(好友:备注优先;群:本地群备注优先,公共群固定原名) ----

    private string FriendDisplayName(FriendInfo f) => f.Remark.Length > 0 ? f.Remark : f.Nickname;

    /// <summary>群显示名:云端群备注优先(消息左侧列表 + 消息栏上方都显示备注),
    /// 无备注显示群名;公共群固定原名。</summary>
    private string GroupDisplayName(GroupInfo g)
    {
        if (g.GroupId == "public")
            return g.Name;
        var remark = GroupRemarkOf(g.GroupId);
        return remark.Length > 0 ? remark : g.Name;
    }

    // ---- 数据快照与列表刷新 ----

    /// <summary>welcome 全量快照:重建好友/群/申请数(重连后也走这里)。</summary>
    private void ApplySnapshot(AuthSnapshot snap)
    {
        _friends.Clear();
        _friends.AddRange(snap.Friends);
        _groups.Clear();
        _groups.AddRange(snap.Groups);
        // 徽章 = 待处理好友申请 + 待我审批的加群申请 + 待我同意的群邀请
        _pendingFriendCount = snap.Requests.Count;
        _pendingGroupCount = snap.GroupRequests.Count;
        _pendingInviteCount = snap.GroupInvites.Count;
        // 已读游标:服务器落库的快照(重连/重启后我的消息已读标记不丢)
        _convReadSeq.Clear();
        foreach (var s in snap.ReadStates)
        {
            var key = $"{s.ConvType}:{s.ConvId}";
            if (!_convReadSeq.TryGetValue(key, out var readers))
                _convReadSeq[key] = readers = new Dictionary<string, long>();
            if (s.Seq > readers.GetValueOrDefault(s.ReaderUserId))
                readers[s.ReaderUserId] = s.Seq;
        }
        // 未读红点:服务器权威(welcome 全量),只保留当前好友/群存在的会话
        // (删除好友/退群后服务器残留计数不显示,页签求和不会虚高)
        _unread.Clear();
        foreach (var (k, v) in snap.UnreadCounts)
        {
            var parts = k.Split(':');
            if (parts.Length != 2)
                continue;
            var exists = parts[0] == "1"
                ? _groups.Any(g => g.GroupId == parts[1])
                : _friends.Any(f => f.UserId == parts[1]);
            if (exists && !_hiddenConvs.Contains(k)) // 已删除会话:重连全量也不恢复红点
                _unread[k] = v;
        }
        RefreshConvList();
        RefreshFriendList();
        RefreshGroupList();
        RefreshRequestBadge();
        if (_convOpen)
            RefreshConvHeader(); // 已打开的会话:快照到达后补刷新头部(重连 welcome 也走这里)
    }

    /// <summary>离线模式:用本地缓存消息灌入内存容器(不重复拉历史,Seq 去重与在线一致)。</summary>
    private void ApplyCachedMessages(LocalCache? cache)
    {
        if (cache is null)
            return;
        _all.Clear();
        _seen.Clear();
        _convMaxSeq.Clear();
        foreach (var m in cache.Messages)
        {
            if (!_seen.Add(m.Seq))
                continue;
            _all.Add(m);
            var key = $"{m.ConvType}:{m.ConvId}";
            if (m.Seq > _convMaxSeq.GetValueOrDefault(key))
                _convMaxSeq[key] = m.Seq;
        }
        _historyLoaded = true; // 离线:不再拉历史(在线路径的 HistoryLoaded 不会触发)
        _historyDone = true;
        RebuildPreview();
        RebuildView();
        RefreshConvList();
    }

    /// <summary>离线模式横幅(顶部,在线/离线切换时更新)。</summary>
    private void ShowOfflineBanner(bool visible, string text)
    {
        OfflineBanner.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        OfflineText.Text = text;
    }

    /// <summary>左下资料卡状态点:在线模式连接时绿,离线/断网灰。</summary>
    private void UpdateMyStatusDot()
    {
        MyStatusDot.Fill = _client.IsConnected && !_client.IsOffline ? UiPalette.Green : Brushes.Silver;
    }
}
