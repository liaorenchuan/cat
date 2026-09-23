using QPet.Core;
using QPet.Mobile.Models;
using QPet.Mobile.Services;
using QPet.Mobile.Views;

namespace QPet.Mobile;

/// <summary>
/// 主界面壳 + 会话镜像大脑(桌面 ChatView 的移动端对应):
/// 底部四页签(消息/好友/群聊/宠物);好友/群/未读/会话预览等镜像随 SyncClient 事件维护,
/// 数据为真(服务器 welcome 全量 + 增量事件),页面只做展示(重建列表,不做 MVVM)。
/// 页签红点:消息 = 会话未读总和,好友 = 好友申请数,群聊 = 群申请 + 群邀请(0 隐藏,99+ 封顶)。
/// 连接检测秒表:断线时横幅提示自动重连,好友在线点全部置灰。
/// </summary>
public partial class MainPage : ContentPage
{
	// 面板与页签按钮一一对应(文字随按钮整体变色加粗)
	private readonly View[] _hosts;
	private readonly Button[] _tabs;
	private readonly Border[] _badges;
	private readonly Label[] _badgeTexts;
	private readonly IDispatcherTimer _connTimer;

	// ---- 会话镜像(服务器 welcome 全量重建 + 事件增量;与桌面 ChatView 同构) ----
	private readonly List<FriendInfo> _friends = [];
	private readonly List<GroupInfo> _groups = [];
	private readonly Dictionary<string, int> _unread = [];            // key "{convType}:{convId}"
	private readonly Dictionary<string, ChatMessage> _lastPreview = [];
	private readonly Dictionary<string, long> _convMaxSeq = [];       // 各会话最大 Seq(T3 聊天页清零用)
	private readonly HashSet<string> _hidden = [];                    // 已删除会话(新消息来才恢复)
	private int _pendingF;   // 待我处理的好友申请(好友页徽章)
	private int _pendingG;   // 待我审批的加群申请(群聊页徽章)
	private int _pendingI;   // 待我同意的群邀请(群聊页徽章)

	private IDispatcherTimer? _hintTimer; // 顶部轻提示自动隐藏计时(连发提示从头计时)

	// ---- 列表刷新合并(C1) ----
	private IDispatcherTimer? _mergeTimer; // 100ms 窗口:窗口内的多次刷新请求合成一次(消息高峰期不再每条重建)
	private bool _dirtyConv, _dirtyFriend, _dirtyGroup; // 窗口内待刷新的列表
	private string? _sigConv, _sigFriend, _sigGroup;     // 上次重建的行签名(内容没变就不重绑 ItemsSource,滚动位置不被重置)

	private bool _live;           // 已订阅 SyncClient(页面隐藏期间退订,防泄漏/空转)
	private bool _historyLoaded;  // 全量历史只拉一次(换账号的新实例会重置)
	private bool _historyDone;    // 历史补拉完成:期间消息只累积,完成一次性刷新(防批量卡顿)
	private bool _connOn;         // 最近一次连接状态(秒表比较变化)
	private bool _connOffline;    // 最近一次离线模式
	private bool _connInit;       // 连接横幅首帧强刷(初始态与默认值相同也要显示)

	public MainPage()
	{
		InitializeComponent();
		_hosts = [ConvHost, FriendHost, GroupHost, PetHost];
		_tabs = [TabMsgButton, TabFriendButton, TabGroupButton, TabPetButton];
		_badges = [MsgBadge, FriendBadge, GroupBadge];
		_badgeTexts = [MsgBadgeText, FriendBadgeText, GroupBadgeText];
		SelectTab(0);

		// 连接检测秒表(1s):断线/恢复时刷新横幅与在线点;SyncClient 无连接事件,轻量轮询
		_connTimer = Dispatcher.CreateTimer();
		_connTimer.Interval = TimeSpan.FromSeconds(1);
		_connTimer.Tick += (_, _) => CheckConnection();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		if (!_live)
		{
			_live = true;
			SubscribeClient();
			// 数据兜底:welcome 快照可能已到(认证后立即导航)或未到,ApplySnapshot 幂等
			ApplySnapshot(AppState.Client.LastSnapshot);
			RefreshMyCard();
			if (AppState.Client.IsOffline)
			{
				// 离线模式:不订阅服务器事件不拉历史,直接用本地缓存灌会话预览
				ApplyCachedMessages(AppState.Client.Cache);
			}
			else if (!_historyLoaded)
			{
				_historyLoaded = true;
				_historyDone = false;
				_ = AppState.Client.LoadHistoryAsync(); // 首次全量历史(只做一次,页面来回切不重复拉)
			}
			CheckConnection();
		}
		// 回到主界面(聊天/详情页弹回):列表/徽章取最新镜像兜底
		RefreshAllLists();
		RefreshBadges();
		// 秒表无条件起(重复 Start 只是重开计时,幂等):以前只在首次进页面时 Start,
		// 而 OnDisappearing 无条件 Stop —— 进过聊天页再回来,横幅就再也不更新了(断线也不提示)
		_connTimer.Start();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		// 真正离开(被踢/登出弹回登录根):退订停表;仅是 push 聊天页等覆盖:保持订阅(镜像持续累积)
		if (Shell.Current.CurrentPage is MainPage)
			return;
		// 只有 MainPage 已不在导航栈(被踢/登出弹空栈)才算真正离开;
		// push 聊天页等只是覆盖(OnDisappearing 时 CurrentPage 已是新页面),保持订阅。
		// 停表也一并挪到这里:被覆盖时不能停,否则回主界面后连不上也看不到横幅(A2)
		if (_live && !Shell.Current.Navigation.NavigationStack.Contains(this))
		{
			_live = false;
			_connTimer.Stop();
			UnsubscribeClient();
		}
	}

	// 安卓返回键:主界面是登录后的唯一落点,不允许退回登录页(LoginPage.OnAppearing 登录态下也会跳回,双保险)。
	// 先收消息页当前展开的弹层(资料卡菜单/输入卡);没有待收的 = 返回即退出应用(Android 习惯)
	protected override bool OnBackButtonPressed()
	{
		if (ConvHost.ConsumeBack())
			return true;
		if (OperatingSystem.IsAndroid())
		{
			Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Finish();
			return true;
		}
		return base.OnBackButtonPressed();
	}

	// ---- SyncClient 订阅(登录会话期全程;页面被弹掉才退) ----

	void SubscribeClient()
	{
		var c = AppState.Client;
		c.WelcomeReceived += OnWelcome;
		c.ChatReceived += OnMessage;
		c.UnreadUpdated += OnUnreadUpdated;
		c.UsersReceived += OnUsersChanged;
		c.FriendAdded += OnFriendAdded;
		c.FriendDeleted += OnFriendDeleted;
		c.GroupUpdated += OnGroupUpdated;
		c.GroupRemoved += OnGroupRemoved;
		c.ProfileUpdated += OnProfileUpdated;
		c.HistoryLoaded += OnHistoryLoaded;
		c.HistoryLoadFailed += OnHistoryLoadFailed;
		c.FriendRequestReceived += OnFriendReqReceived;
		c.FriendRequestResultReceived += OnFriendReqResult;
		c.RequestHandled += OnFriendReqHandled;
		c.GroupRequestReceived += OnGroupReqReceived;
		c.GroupRequestResultReceived += OnGroupReqResult;
		c.GroupRequestHandled += OnGroupReqHandled;
		c.GroupInviteReceived += OnGroupInviteReceived;
		c.GroupInviteResultReceived += OnGroupInviteResult;
		c.GroupInviteHandled += OnGroupInviteHandled;
		c.SettingReceived += OnSettingReceived;
	}

	void UnsubscribeClient()
	{
		var c = AppState.Client;
		c.WelcomeReceived -= OnWelcome;
		c.ChatReceived -= OnMessage;
		c.UnreadUpdated -= OnUnreadUpdated;
		c.UsersReceived -= OnUsersChanged;
		c.FriendAdded -= OnFriendAdded;
		c.FriendDeleted -= OnFriendDeleted;
		c.GroupUpdated -= OnGroupUpdated;
		c.GroupRemoved -= OnGroupRemoved;
		c.ProfileUpdated -= OnProfileUpdated;
		c.HistoryLoaded -= OnHistoryLoaded;
		c.HistoryLoadFailed -= OnHistoryLoadFailed;
		c.FriendRequestReceived -= OnFriendReqReceived;
		c.FriendRequestResultReceived -= OnFriendReqResult;
		c.RequestHandled -= OnFriendReqHandled;
		c.GroupRequestReceived -= OnGroupReqReceived;
		c.GroupRequestResultReceived -= OnGroupReqResult;
		c.GroupRequestHandled -= OnGroupReqHandled;
		c.GroupInviteReceived -= OnGroupInviteReceived;
		c.GroupInviteResultReceived -= OnGroupInviteResult;
		c.GroupInviteHandled -= OnGroupInviteHandled;
		c.SettingReceived -= OnSettingReceived;
	}

	// ---- 数据镜像:welcome 全量 / 事件增量(桌面 ApplySnapshot/事件处理同构) ----

	void OnWelcome(AuthSnapshot snap) => ApplySnapshot(snap);

	void ApplySnapshot(AuthSnapshot snap)
	{
		_friends.Clear();
		_friends.AddRange(snap.Friends);
		_groups.Clear();
		_groups.AddRange(snap.Groups);
		// 徽章 = 待处理好友申请 + 待我审批的加群申请 + 待我同意的群邀请
		_pendingF = snap.Requests.Count;
		_pendingG = snap.GroupRequests.Count;
		_pendingI = snap.GroupInvites.Count;
		// 未读红点:服务器权威(welcome 全量);只保留当前好友/群存在的会话,
		// 已删除会话:重连全量也不恢复红点(桌面同规则)
		_unread.Clear();
		foreach (var (k, v) in snap.UnreadCounts)
		{
			var parts = k.Split(':');
			if (parts.Length != 2)
				continue;
			var exists = parts[0] == "1"
				? _groups.Any(g => g.GroupId == parts[1])
				: _friends.Any(f => f.UserId == parts[1]);
			if (exists && !_hidden.Contains(k))
				_unread[k] = v;
		}
		RefreshAllLists(); // 好友/群/未读都换了:三张表都要重建
		RefreshBadges();
	}

	void OnMessage(ChatMessage m)
	{
		var key = $"{m.ConvType}:{m.ConvId}";
		_lastPreview[key] = m;
		if (m.Seq > _convMaxSeq.GetValueOrDefault(key))
			_convMaxSeq[key] = m.Seq;
		_hidden.Remove(key); // 已删除会话:有新消息(含自己发的)即重新出现(继续走下方刷新)
		if (!_historyDone)
			return; // 历史补拉期间只累积,HistoryLoaded 一次性刷新(防批量卡顿)
		RequestRefresh(conv: true); // 只影响消息页那张表(以前三张表全重建,滚动位置还会弹回顶部)
	}

	void OnUnreadUpdated(string convKey, int count)
	{
		if (_hidden.Contains(convKey)) return; // 已删除会话:红点不显示(也不进页签求和)
		_unread[convKey] = count;
		RequestRefresh(conv: true);
		RefreshBadges();
	}

	void OnUsersChanged(List<UserInfo> users)
	{
		// 在线状态刷新(会话列表好友条目在线点)
		var onlineIds = users.Select(u => u.UserId).ToHashSet();
		foreach (var f in _friends)
			f.Online = onlineIds.Contains(f.UserId);
		RequestRefresh(conv: true, friend: true); // 在线点同时体现在会话行与好友行
	}

	void OnFriendAdded(FriendInfo friend)
	{
		// 合并进现有列表(重连 welcome 与实时推送重叠不重复加)
		if (_friends.All(x => x.UserId != friend.UserId))
		{
			_friends.Add(friend);
			RequestRefresh(conv: true, friend: true);
		}
	}

	void OnFriendDeleted(string userId)
	{
		_friends.RemoveAll(x => x.UserId == userId);
		var key = $"{Protocol.ConvKeyPrivate}{userId}";
		_unread.Remove(key);
		_lastPreview.Remove(key);
		RequestRefresh(conv: true, friend: true);
		RefreshBadges();
	}

	void OnGroupUpdated(GroupInfo group)
	{
		// 全字段同步(改名/加人/退人/设管理都走这里),不存在则新增(自己刚建的群)
		var existing = _groups.FirstOrDefault(x => x.GroupId == group.GroupId);
		if (existing is null)
		{
			_groups.Add(group);
		}
		else
		{
			existing.Members = group.Members;
			existing.Num = group.Num;
			existing.Name = group.Name;
			existing.IsPublic = group.IsPublic;
			existing.OwnerId = group.OwnerId;
			existing.AdminIds = group.AdminIds;
		}
		RequestRefresh(conv: true, group: true); // 群名/人数/备注:会话行与群聊页同源
	}

	void OnGroupRemoved(string groupId, string reason)
	{
		// 群被解散或我被移出:从列表与会话移除(会话不再出现)
		_groups.RemoveAll(x => x.GroupId == groupId);
		var key = $"{Protocol.ConvKeyGroup}{groupId}";
		_hidden.Add(key);
		_unread.Remove(key);
		_lastPreview.Remove(key);
		RequestRefresh(conv: true, group: true);
		RefreshBadges();
	}

	void OnProfileUpdated(string userId, string nickname)
	{
		if (userId == AppState.Client.MyUser.UserId)
		{
			// 自己昵称被服务器确认(本端改昵称/其他端改):资料卡同步
			AppState.Client.MyUser.Nickname = nickname;
			RefreshMyCard();
			return;
		}
		var f = _friends.FirstOrDefault(x => x.UserId == userId);
		if (f is not null)
		{
			f.Nickname = nickname;
			RequestRefresh(conv: true, friend: true); // 昵称同时出现在会话行与好友行
		}
	}

	void OnHistoryLoaded()
	{
		_historyDone = true;
		RefreshConvList();
	}

	/// <summary>历史补拉重试后仍失败(B3):顶部提示,别让用户以为"记录就是这么少"。
	/// 补拉失败的会话列表不清空,已有的镜像数据照常显示。</summary>
	void OnHistoryLoadFailed(string reason) => ShowHint(reason);

	// ---- 页签徽章计数(申请/邀请推送增减,页面内列表刷新由 T4/T5 页面自理) ----

	void OnSettingReceived(string key, string value)
	{
		// 云端群备注(自己/其他设备改动回推):群显示名变了,会话与群列表一并重建
		if (key.StartsWith("GroupRemark.", StringComparison.Ordinal))
			RequestRefresh(conv: true, group: true);
	}

	void OnFriendReqReceived(FriendRequest req) { _pendingF++; RefreshBadges(); }
	void OnFriendReqResult(long requestId, bool accepted, string fromUserId, string fromName)
	{
		// 我的好友申请有结果:同意 = 已是好友(FriendAdded 已刷列表);拒绝 = 顶部提示
		if (!accepted)
			ShowHint($"{fromName} 拒绝了你的好友申请");
	}
	void OnFriendReqHandled(long requestId) { if (_pendingF > 0) _pendingF--; RefreshBadges(); }
	void OnGroupReqReceived(GroupRequest req) { _pendingG++; RefreshBadges(); }
	void OnGroupReqResult(long requestId, bool accepted, string groupId, string groupName)
	{
		// 我的加群申请有结果:同意 = 群已由 GroupUpdated 推送加入列表;拒绝 = 顶部提示(文案同桌面)
		if (!accepted)
			ShowHint($"「{groupName}」的加入申请被拒绝");
	}
	void OnGroupReqHandled(long requestId) { if (_pendingG > 0) _pendingG--; RefreshBadges(); }
	void OnGroupInviteReceived(GroupInvite invite) { _pendingI++; RefreshBadges(); }
	void OnGroupInviteResult(long inviteId, bool accepted, string groupId, string groupName)
	{
		// 我发出的邀请有结果:同意 = 进群由 GroupUpdated 推送;拒绝 = 顶部提示(文案同桌面)
		if (!accepted)
			ShowHint($"「{groupName}」的邀请被拒绝");
	}
	void OnGroupInviteHandled(long inviteId) { if (_pendingI > 0) _pendingI--; RefreshBadges(); }

	// ---- 离线模式:用本地缓存消息灌会话预览(桌面 ApplyCachedMessages 同构) ----

	void ApplyCachedMessages(LocalCache cache)
	{
		_lastPreview.Clear();
		_convMaxSeq.Clear();
		foreach (var m in cache.Messages)
		{
			var key = $"{m.ConvType}:{m.ConvId}";
			_lastPreview[key] = m;
			if (m.Seq > _convMaxSeq.GetValueOrDefault(key))
				_convMaxSeq[key] = m.Seq;
		}
		_historyDone = true; // 离线:不再拉历史(在线路径的 HistoryLoaded 不会触发)
		RefreshAllLists();
	}

	// ---- UI 刷新 ----

	/// <summary>顶部轻提示(申请/邀请被拒等一次性结果;同连接横幅视觉,3 秒自动隐藏)。</summary>
	void ShowHint(string text)
	{
		HintText.Text = text;
		HintBanner.IsVisible = true;
		if (_hintTimer is null)
		{
			_hintTimer = Dispatcher.CreateTimer();
			_hintTimer.Interval = TimeSpan.FromSeconds(3);
			_hintTimer.Tick += (_, _) =>
			{
				_hintTimer.Stop();
				HintBanner.IsVisible = false;
			};
		}
		_hintTimer.Stop(); // 连续两条提示:从最新一条开始计时
		_hintTimer.Start();
	}

	/// <summary>重建会话列表(消息页;好友+群合并规则见 ConvFeed)。
	/// 内容与上次一致时不重绑 ItemsSource —— 重绑会把滚动位置弹回顶部,Tab 上又没法手工恢复。</summary>
	void RefreshConvList()
	{
		var connected = AppState.Client.IsConnected && !AppState.Client.IsOffline;
		var settings = AppState.Client.LastSnapshot.Settings;
		var rows = ConvFeed.Build(_groups, _friends, _unread, _lastPreview, _hidden, connected,
			AppState.Client.MyUser.UserId, settings);
		var sig = string.Join('\n', rows.Select(r => r.Signature));
		if (sig != _sigConv)
		{
			_sigConv = sig;
			ConvHost.Reload(rows);
		}
		RefreshBadges();
	}

	/// <summary>重建好友页列表(在线点/备注与消息页同镜像)。</summary>
	void RefreshFriendList()
	{
		var connected = AppState.Client.IsConnected && !AppState.Client.IsOffline;
		var rows = FriendFeed.Build(_friends, connected);
		var sig = string.Join('\n', rows.Select(r => r.Signature));
		if (sig != _sigFriend)
		{
			_sigFriend = sig;
			FriendHost.Reload(rows);
		}
	}

	/// <summary>重建群聊页列表(备注/人数与消息页群会话行同镜像)。</summary>
	void RefreshGroupList()
	{
		var rows = GroupFeed.Build(_groups, AppState.Client.LastSnapshot.Settings);
		var sig = string.Join('\n', rows.Select(r => r.Signature));
		if (sig != _sigGroup)
		{
			_sigGroup = sig;
			GroupHost.Reload(rows);
		}
	}

	/// <summary>三张列表全刷(登录快照、连接状态变化、回到主界面等镜像大改的场合)。</summary>
	void RefreshAllLists()
	{
		RefreshConvList();
		RefreshFriendList();
		RefreshGroupList();
	}

	/// <summary>事件驱动的刷新入口:100ms 窗口内的多次请求合并成一次。
	/// 消息高峰期不再"每条消息推倒三张表",页面只刷真正受影响的那张。</summary>
	void RequestRefresh(bool conv = false, bool friend = false, bool group = false)
	{
		_dirtyConv |= conv;
		_dirtyFriend |= friend;
		_dirtyGroup |= group;
		if (_mergeTimer is null)
		{
			_mergeTimer = Dispatcher.CreateTimer();
			_mergeTimer.Interval = TimeSpan.FromMilliseconds(100);
			_mergeTimer.Tick += (_, _) =>
			{
				_mergeTimer!.Stop(); // 一次性:下一批请求重新开窗口
				FlushRefresh();
			};
		}
		if (!_mergeTimer.IsRunning)
			_mergeTimer.Start();
	}

	void FlushRefresh()
	{
		var (conv, friend, group) = (_dirtyConv, _dirtyFriend, _dirtyGroup);
		_dirtyConv = _dirtyFriend = _dirtyGroup = false;
		if (conv) RefreshConvList();
		if (friend) RefreshFriendList();
		if (group) RefreshGroupList();
	}

	/// <summary>重算三枚页签徽章(消息 = 会话未读和 / 好友 = 好友申请 / 群 = 群申请 + 邀请)。
	/// 公开:聊天页/申请中心等就地改动时可直接调。</summary>
	public void RefreshBadges()
	{
		var n = 0;
		foreach (var v in _unread.Values) n += v;
		SetBadge(0, n);
		SetBadge(1, _pendingF);
		SetBadge(2, _pendingG + _pendingI);
		FriendHost.SetReqDot(_pendingF); // 好友页"📮 好友申请"角标(与页签红点同源)
		GroupHost.SetReqDot(_pendingG, _pendingI); // 群聊页"群申请/群邀请"角标(与页签红点同源)
	}

	/// <summary>资料卡内容(MainPage 调用;ConvPanel 改昵称成功后也调一次)。</summary>
	public void RefreshMyCard()
	{
		var on = AppState.Client.IsConnected && !AppState.Client.IsOffline;
		ConvHost.SetMyInfo(AppState.Client.MyUser.Nickname, AppState.Account, on);
	}

	/// <summary>左滑关闭会话:本地隐藏(移除预览与未读;新消息来自动恢复)。桌面 OnDeleteConvMenu 同构。</summary>
	public void CloseConvLocal(string key)
	{
		_hidden.Add(key);
		_unread.Remove(key);
		_lastPreview.Remove(key);
		_convMaxSeq.Remove(key);
		RefreshConvList();
		RefreshBadges();
	}

	/// <summary>全量历史是否仍在补拉中(聊天页打开会话时查询:补拉期间消息只累积不逐条刷)。</summary>
	public bool HistoryPending => !_historyDone;

	/// <summary>会话被打开:本地未读清零 + 重建(服务器收到 SendRead 后回推 unreadUpdate 0,双保险幂等)。
	/// 桌面 SelectConv 同构:正在看 = 已读。</summary>
	public void MarkConvOpened(string key)
	{
		_unread[key] = 0;
		RefreshConvList();
		RefreshBadges();
	}

	/// <summary>好友详情"发消息"等从会话外打开私聊:若该会话此前左滑关闭,先恢复(桌面 ShowConversation 同构)。</summary>
	public void ReopenConv(string key)
	{
		if (!_hidden.Remove(key))
			return;
		RefreshConvList();
		RefreshBadges();
	}

	// 连接检测:状态变化时刷横幅与在线点(断网:全部置灰)
	void CheckConnection()
	{
		var on = AppState.Client.IsConnected && !AppState.Client.IsOffline;
		var off = AppState.Client.IsOffline;
		if (_connInit && on == _connOn && off == _connOffline)
			return;
		_connInit = true;
		_connOn = on;
		_connOffline = off;
		ConvHost.SetConnection(on, off);
		RefreshMyCard();
		RefreshAllLists(); // 在线点全灰/复亮:会话行与好友行都要刷
	}

	void SetBadge(int index, int count)
	{
		_badges[index].IsVisible = count > 0;
		_badgeTexts[index].Text = BadgeText(count);
	}

	static string BadgeText(int n) => n > 99 ? "99+" : n.ToString();

	// ---- 页签切换 ----

	void OnTabMsgClicked(object? sender, EventArgs e) => SelectTab(0);
	void OnTabFriendClicked(object? sender, EventArgs e) => SelectTab(1);
	void OnTabGroupClicked(object? sender, EventArgs e) => SelectTab(2);
	void OnTabPetClicked(object? sender, EventArgs e) => SelectTab(3);

	// 页签切换:选中 = 浅橙胶囊底 + 深橙粗字;未选 = 透明底 + 棕色字
	void SelectTab(int index)
	{
		for (var i = 0; i < _hosts.Length; i++)
		{
			var selected = i == index;
			_hosts[i].IsVisible = selected;
			_tabs[i].BackgroundColor = selected ? Color.FromArgb("#FFE9D2") : Colors.Transparent;
			_tabs[i].TextColor = selected ? Color.FromArgb("#C4702F") : Color.FromArgb("#B08A62");
			_tabs[i].FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None;
		}
	}
}
