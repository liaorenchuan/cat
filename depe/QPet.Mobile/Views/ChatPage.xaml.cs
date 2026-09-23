using System.Collections.ObjectModel;
using QPet.Core;
using QPet.Mobile.Models;
using QPet.Mobile.Services;
using QPet.Mobile.Utils;
using QPet.Mobile.Views.Controls;

namespace QPet.Mobile.Views;

/// <summary>
/// 聊天详情页(对齐桌面 ChatView 消息区):真实会话(服务器数据驱动)。
/// 由会话行携带 title/key/isGroup 路由参数进入,key = 协议会话键 "0:对方UserId" / "1:群Id";
/// 消息源 = SyncClient.Cache.Messages 全量镜像(seq 升序去重,5000 上限,已由 Core 维护),
/// 只过滤当前会话重建视图;日期栏/已读标/在线点/发送上报逐字对齐桌面 ChatView.Messages/Input。
/// 已读游标本地维护(只进不退):打开时从 welcome ReadStates 灌入,readStates/read 帧增量;
/// 打开会话与收到新消息即 SendRead(服务器未读归零并回推 unreadUpdate 0)。
/// 头部动作:私聊 备注(桌面"好友备注"同款文案)/删除好友;群聊 退群(群主=解散)/成员/改名/
/// 群备注(云端,换设备也有),公共群固定原名无 退群/改名/备注(桌面同规则);
/// 群聊气泡头像可点:弹用户主页卡(GetUserProfile,加好友);私聊头像 → 好友详情页。
/// </summary>
public partial class ChatPage : ContentPage, IQueryAttributable
{
	// 消息视图(ObservableCollection:增量 Add 只动新行,不整列重建——整列重建 + 滚底动画叠加是消息到达时列表乱闪的根源)
	private readonly ObservableCollection<ChatMsgRow> _rows = [];
	private ChatMessage? _viewLastMsg;          // 上一条已入视图消息(跨天插日期栏)
	private long _convLastSeq;                  // 本会话最大 Seq(打开/历史完成时 SendRead 上报)

	// 已读游标:convKey("0:x"/"1:x") → 读方 UserId → 已读到 Seq(只进不退,桌面同规则)
	private readonly Dictionary<string, Dictionary<string, long>> _readSeq = [];

	private int _convType;       // 0=私聊 1=群聊
	private string _convId = "";
	private string _key = "";                // 协议会话键
	private string _myId = "";
	private string _displayTitle = "";       // 路由标题(会话列表备注/群名,头部查不到时兜底)
	private string _profilePendingId = "";   // 主页卡请求目标(GetUserProfile 异步回,过期响应丢弃)
	private bool _isGroup;

	private bool _live;              // 已订阅 SyncClient(被覆盖不退订,弹掉才退)
	private bool _closing;           // 正在退出本页(删除好友/退群 与 服务器事件可能各触发一次返回)
	private bool _pendingHistory;    // 全量历史补拉中:期间只累积,HistoryLoaded 统一重建
	private string _confirmAction = "";   // 确认卡当前用途(删除好友/退群)
	private string _promptAction = "";    // 输入卡当前用途(备注/群改名)

	public ChatPage()
	{
		InitializeComponent();
		MsgList.ItemsSource = _rows;
		BindableLayout.SetItemsSource(EmojiFlex, Emojis);
	}

	// 路由参数:chat?title=会话标题&key=协议键&isGroup=true/false(键即真相,isGroup 仅旧参数兼容)
	public void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		_displayTitle = query.TryGetValue("title", out var t) ? Uri.UnescapeDataString(t.ToString() ?? "") : "";
		_key = query.TryGetValue("key", out var k) ? k.ToString() ?? "" : "";
		_isGroup = _key.StartsWith(Protocol.ConvKeyGroup);
		_convType = _isGroup ? 1 : 0;
		_convId = _key.Length > 2 ? _key[2..] : "";
		_confirmAction = "";
		_promptAction = "";
		_closing = false;
		CloseOverlays();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		if (_convId.Length == 0)
			return;
		if (_live)
		{
			// 从成员页等覆盖页返回:期间群名/成员/备注可能被改(事件已同步镜像),取最新刷新
			RefreshHeader();
			ScrollToEnd();
			return;
		}
		_live = true;
		_myId = AppState.Client.MyUser.UserId;
		SubscribeClient();
		InitReadSeq(); // 打开会话前服务器全量已读游标(welcome),只进不退
		RefreshHeader();
		var main = FindMainPage();
		_pendingHistory = main?.HistoryPending ?? false; // 首次登录历史补拉中:期间只累积
		RebuildView();
		main?.MarkConvOpened(_key); // 正在看 = 未读即时清零(服务器收到 SendRead 后回推 0)
		if (!_pendingHistory && _convLastSeq > 0 && AppState.Client.IsConnected)
			AppState.Client.SendRead(_convType, _convId, _convLastSeq); // 打开会话即已读全部
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		// 真正离开(弹回列表)退订;仅被成员页/好友详情等覆盖(本页之上还有页面)保持订阅(消息/已读实时维护)。
		// 判定统一走 NavGuard:不能用 Contains(this) —— pop 动画期间栈可能尚未移除本页,
		// 会被误判成"被覆盖"而残留订阅,残留的 ChatPage 会把退出后到达的新消息继续 SendRead 清未读
		if (_live && !NavGuard.IsCovered(this))
		{
			_live = false;
			UnsubscribeClient();
		}
	}

	// Android 返回键:先收面板/弹层,再退出页面
	protected override bool OnBackButtonPressed()
	{
		if (Overlay.IsVisible)
		{
			CloseOverlays();
			return true;
		}
		if (EmojiPanel.IsVisible)
		{
			ToggleEmojiPanel();
			return true;
		}
		return base.OnBackButtonPressed();
	}

	// ---- SyncClient 订阅(页面打开期间) ----

	void SubscribeClient()
	{
		var c = AppState.Client;
		c.ChatReceived += OnMessage;
		c.ReadReceived += OnRead;
		c.ReadStatesReceived += OnReadStates;
		c.HistoryLoaded += OnHistoryLoaded;
		c.ErrorReceived += OnError;
		c.ProfileUpdated += OnProfileUpdated;
		c.GroupUpdated += OnGroupUpdated;
		c.GroupRemoved += OnGroupRemoved;
		c.FriendDeleted += OnFriendDeleted;
		c.SettingReceived += OnSettingReceived;      // 群备注回推:头部标题按备注优先刷新
		c.UserProfileReceived += OnUserProfileReceived; // 群头像主页卡(异步结果,过期丢弃)
	}

	void UnsubscribeClient()
	{
		var c = AppState.Client;
		c.ChatReceived -= OnMessage;
		c.ReadReceived -= OnRead;
		c.ReadStatesReceived -= OnReadStates;
		c.HistoryLoaded -= OnHistoryLoaded;
		c.ErrorReceived -= OnError;
		c.ProfileUpdated -= OnProfileUpdated;
		c.GroupUpdated -= OnGroupUpdated;
		c.GroupRemoved -= OnGroupRemoved;
		c.FriendDeleted -= OnFriendDeleted;
		c.SettingReceived -= OnSettingReceived;
		c.UserProfileReceived -= OnUserProfileReceived;
	}

	static MainPage? FindMainPage() =>
		Shell.Current.Navigation.NavigationStack.OfType<MainPage>().LastOrDefault();

	// ---- 消息(来源 SyncClient.Cache.Messages 全量镜像;桌面 OnMessage/RebuildView 同构) ----

	void OnMessage(ChatMessage m)
	{
		if (m.ConvType != _convType || m.ConvId != _convId)
			return; // 非当前会话:预览/未读由 MainPage 镜像维护,这里不关心
		if (_pendingHistory)
			return; // 历史补拉期间只累积(Cache 已收),HistoryLoaded 统一重建(防批量卡顿)
		AppendRow(m);
		UpdateReadMarkers(); // 新消息(含自己发的)重算已读/未读标记
		ScrollToEnd();      // 行级增量已上屏,只滚到底(无动画)
		if (AppState.Client.IsConnected)
			AppState.Client.SendRead(_convType, _convId, m.Seq); // 正在看 = 新消息立即已读
	}

	/// <summary>视图整体重建(打开会话/历史补拉完成):先解绑再改集合,一次重绑上屏(避免逐行通知中间态)。</summary>
	void RebuildView()
	{
		MsgList.ItemsSource = null; // 解绑:清空/填充期间不触发逐行 UI 更新
		_rows.Clear();
		_viewLastMsg = null;
		_convLastSeq = 0;
		foreach (var m in AppState.Client.Cache.Messages)
		{
			if (m.ConvType == _convType && m.ConvId == _convId)
				AppendRow(m);
		}
		UpdateReadMarkers();
		MsgList.ItemsSource = _rows; // 一次重绑
		ScrollToEnd();
		// Android 上 CollectionView 首次布局完成前的 ScrollTo 会被丢弃(列表停在顶部):
		// 打开会话/历史补拉完成的重绑场景延迟两拍兜底,确保最终停在最新消息
		Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(150), () =>
		{ if (_live) ScrollToEnd(); });
		Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(450), () =>
		{ if (_live) ScrollToEnd(); });
	}

	/// <summary>追加一条消息到当前视图:跨天先插日期栏;在线点 = 私聊对方消息(绿=连接且在线,灰=离线/断网)。</summary>
	void AppendRow(ChatMessage m)
	{
		var t = m.Time.ToLocalTime();
		if (_viewLastMsg is null || _viewLastMsg.Time.ToLocalTime().Date != t.Date)
			_rows.Add(new ChatMsgRow { DateText = DateBarText(t.Date) }); // 跨天:日期栏
		var mine = m.Type != 1 && m.FromUserId == _myId;
		var connected = AppState.Client.IsConnected && !AppState.Client.IsOffline;
		var name = m.FromName.Length > 0 ? m.FromName : "?";
		_rows.Add(new ChatMsgRow
		{
			Msg = m,
			IsMine = mine,
			IsGroup = _isGroup,
			Time = t.ToString("HH:mm"),
			Name = name,
			AvatarChar = name[..1],
			AvatarBg = mine ? Color.FromArgb("#FFE4C8") : AvatarPalette.Pick(name),
			AvatarDotVisible = !mine && m.ConvType == 0, // 仅私聊对方消息(桌面 ShowPeerDot=ConvType==0)
			AvatarDotBrush = connected && PeerOnline(m.FromUserId)
				? Color.FromArgb("#58B368") : Color.FromArgb("#C9C9C9"),
		});
		_viewLastMsg = m;
		if (m.Seq > _convLastSeq)
			_convLastSeq = m.Seq;
	}

	/// <summary>对方在线(好友名单含 Online 字段;MainPage 随 users 帧维护共享对象,这里只读)。</summary>
	bool PeerOnline(string userId) =>
		AppState.Client.LastSnapshot.Friends.FirstOrDefault(x => x.UserId == userId)?.Online == true;

	// ---- 已读标记(桌面 UpdateReadMarkers/ReadTextFor/OnRead 同构) ----

	/// <summary>重算已读/未读标记:每条自己的消息都标——有回执显示已读文案,否则"未读"。</summary>
	void UpdateReadMarkers()
	{
		// 群信息按需建一次字典:C2 之前每行都做一次群列表 FirstOrDefault + 成员线性扫描(行数×成员数),
		// 群聊回执风暴时会明显卡顿
		Dictionary<string, GroupInfo>? groups = null;
		foreach (var row in _rows)
		{
			if (row.Msg is not { } m || !row.IsMine)
				continue;
			if (m.ConvType == 1 && groups is null)
				groups = AppState.Client.LastSnapshot.Groups.ToDictionary(g => g.GroupId);
			var text = ReadTextFor(m, groups);
			row.ReadText = text.Length > 0 ? text : "未读";
		}
	}

	/// <summary>一条我的消息的已读文案:私聊 = 对方已读(游标 ≥ 该消息);群聊 = "已读 N/总"(N = 已读的其他成员数)。
	/// groups 由调用方一次建好(私聊消息用不到,允许为 null)。</summary>
	string ReadTextFor(ChatMessage msg, Dictionary<string, GroupInfo>? groups)
	{
		var key = $"{msg.ConvType}:{msg.ConvId}";
		if (!_readSeq.TryGetValue(key, out var readers))
			return "";
		if (msg.ConvType == 0)
		{
			// 私聊:读者 = 对方(会话 ID),游标 ≥ 该消息 Seq 即已读
			return readers.GetValueOrDefault(msg.ConvId) >= msg.Seq ? "已读" : "";
		}
		// 群聊:统计其他成员里游标 ≥ 该消息 Seq 的人数(成员数取当前群快照)
		if (groups is null || !groups.TryGetValue(msg.ConvId, out var group) || group.Members.Count <= 1)
			return "";
		var count = group.Members.Count(m => m.UserId != _myId
			&& readers.GetValueOrDefault(m.UserId) >= msg.Seq);
		return count > 0 ? $"已读 {count}/{group.Members.Count - 1}" : "";
	}

	/// <summary>收到已读回执:会话中某人已读到 seq。正在看该会话 → 重算已读标记。</summary>
	void OnRead(ReadReceipt r)
	{
		if (!MergeReader($"{r.ConvType}:{r.ConvId}", r.FromUserId, r.Seq))
			return; // 过期回执(游标只进不退)
		if (r.ConvType == _convType && r.ConvId == _convId)
			UpdateReadMarkers(); // 行内 ReadText 通知自动刷新,不动列表(回执风暴不再引发重建/滚屏)
	}

	/// <summary>welcome/readStates 帧:全量已读游标静默合并(与 InitReadSeq 双保险,幂等只进不退)。</summary>
	void OnReadStates(List<ReadState> states)
	{
		foreach (var s in states)
			MergeReader($"{s.ConvType}:{s.ConvId}", s.ReaderUserId, s.Seq);
		if (_live)
			UpdateReadMarkers(); // 重连补拉完成:已读标按最新游标重算(行内通知刷新)
	}

	/// <summary>合并已读游标(只进不退)。返回是否真的有推进(旧回执直接忽略)。</summary>
	bool MergeReader(string key, string readerId, long seq)
	{
		if (!_readSeq.TryGetValue(key, out var readers))
			_readSeq[key] = readers = new Dictionary<string, long>();
		if (seq <= readers.GetValueOrDefault(readerId))
			return false;
		readers[readerId] = seq;
		return true;
	}

	/// <summary>打开会话前已收到的服务器全量游标(welcome ReadStates)。</summary>
	void InitReadSeq()
	{
		foreach (var s in AppState.Client.LastSnapshot.ReadStates)
			MergeReader($"{s.ConvType}:{s.ConvId}", s.ReaderUserId, s.Seq);
	}

	/// <summary>全量历史补拉完成(登录后首拉;断线重连补拉在订阅外,页面数据已实时维护)。</summary>
	void OnHistoryLoaded()
	{
		if (!_live || !_pendingHistory)
			return;
		_pendingHistory = false;
		RebuildView(); // 一次重建:补拉期间累积的历史消息全部上屏
		if (_convLastSeq > 0 && AppState.Client.IsConnected)
			AppState.Client.SendRead(_convType, _convId, _convLastSeq); // 正在看 = 历史全已读
	}

	/// <summary>服务器通用错误(发消息失败/操作被拒等):状态条提示。</summary>
	void OnError(string reason) => ShowStatus(reason);

	// ---- 周边数据事件(头像标题/群信息变化时刷新头部与已读分母) ----

	void OnProfileUpdated(string userId, string nickname)
	{
		if (_live && userId == _convId && !_isGroup)
			RefreshHeader(); // 对方昵称被改(无备注时头标题随昵称);自己的昵称不进聊天页头部
	}

	void OnGroupUpdated(GroupInfo group)
	{
		if (!_live || group.GroupId != _convId)
			return;
		RefreshHeader();      // 群名/成员数变化
		UpdateReadMarkers();  // 成员变动:已读 N/总 的分母变了,重算(行内通知刷新)
	}

	void OnGroupRemoved(string groupId, string reason)
	{
		if (_live && _isGroup && groupId == _convId)
			_ = CloseSelfAsync(); // 群被解散/我被移出:回消息列表(会话已从列表移除)
	}

	/// <summary>群备注云端回推(本机另一连接/换设备改的):备注优先的标题即时刷新(桌面 ApplyGroupRemark 同构)。
	/// 备注缓存由 SyncClient 维护;会话列表侧 MainPage 也订阅了本事件各自刷新。</summary>
	void OnSettingReceived(string key, string value)
	{
		if (_live && _isGroup && key == "GroupRemark." + _convId)
			RefreshHeader();
	}

	/// <summary>群头像主页卡结果:与请求目标一致才弹(桌面 OnUserProfileReceived 同款过期保护)。</summary>
	void OnUserProfileReceived(UserProfile profile)
	{
		if (!_live || profile.UserId != _profilePendingId)
			return; // 过期响应(快速连点头像/已离开页面)
		_profilePendingId = "";
		ProfileUi.Bind(profile); // 状态(你自己/已是好友/已申请/陌生人)由卡内切
		OpenCard(ProfileUi);
	}

	/// <summary>群显示名:公共群固定原名,其余云端备注优先(桌面 GroupDisplayName 同构)。</summary>
	static string GroupDisplayName(GroupInfo g, IReadOnlyDictionary<string, string> settings)
	{
		if (g.GroupId == "public")
			return g.Name;
		var remark = settings.GetValueOrDefault("GroupRemark." + g.GroupId) ?? "";
		return remark.Length > 0 ? remark : g.Name;
	}

	void OnFriendDeleted(string userId)
	{
		if (_live && !_isGroup && userId == _convId)
		{
			CloseOverlays();
			_ = CloseSelfAsync(); // 好友已删:本端会话随之关闭
		}
	}

	/// <summary>退回消息列表(A3):Shell 的 ".." 弹的永远是栈顶 —— 本页被成员页/好友详情等覆盖时,
	/// 弹掉的是别人,本页自己赖着不走(会话都没了,再发消息必报错,而且不会有第二次通知)。
	/// 这里改成"逐层弹到本页为止",结果与栈顶是谁无关。</summary>
	async Task CloseSelfAsync()
	{
		if (_closing)
			return; // 删除好友/退群 与 服务器事件可能先后各触发一次,只认第一次
		_closing = true;
		_live = false;
		UnsubscribeClient(); // 先退订:这次返回过程中的事件不再触发第二次导航
		var navigation = Shell.Current.Navigation;
		while (navigation.NavigationStack.Contains(this))
		{
			if (ReferenceEquals(navigation.NavigationStack[^1], this))
			{
				await Shell.Current.GoToAsync("..");
				return; // 自己在栈顶:弹掉自己即收工(不继续循环,防把主界面也弹掉)
			}
			var before = navigation.NavigationStack.Count;
			await Shell.Current.GoToAsync(".."); // 上面还压着页面(成员页/好友详情):先弹掉它
			if (navigation.NavigationStack.Count >= before)
				return; // 栈没变(导航没生效):收手,绝不继续弹
		}
	}

	// ---- 会话头(标题/副行/状态点/动作按钮;桌面 ChatView 头部信息同构) ----

	/// <summary>按最新快照刷新头部:私聊 = 备注优先的标题 + 对方在线点;群聊 = 备注优先的显示名 +
	/// 群号/成员数。公共群:固定原名,无退群/改名/备注按钮(桌面同规则),成员入口保留。</summary>
	void RefreshHeader()
	{
		if (_convType == 0)
		{
			var friend = AppState.Client.LastSnapshot.Friends.FirstOrDefault(x => x.UserId == _convId);
			var connected = AppState.Client.IsConnected && !AppState.Client.IsOffline;
			var name = friend is null ? _displayTitle
				: (friend.Remark.Length > 0 ? friend.Remark : friend.Nickname);
			HeadTitle.Text = name.Length > 0 ? name : _displayTitle;
			HeadSub.Text = "私聊";
			HeadDot.IsVisible = true;
			HeadDot.Fill = connected && friend?.Online == true
				? Color.FromArgb("#58B368") : Color.FromArgb("#C9C9C9");
			PrivActions.IsVisible = true;
			GroupActions.IsVisible = false;
		}
		else
		{
			var group = AppState.Client.LastSnapshot.Groups.FirstOrDefault(x => x.GroupId == _convId);
			var isPublic = group?.IsPublic == true;
			HeadTitle.Text = group is null ? _displayTitle
				: GroupDisplayName(group, AppState.Client.LastSnapshot.Settings);
			HeadSub.Text = group is null ? "群聊"
				: group.Num.Length > 0 ? $"群号 {group.Num} · 成员 {group.Members.Count} 人"
				: $"成员 {group.Members.Count} 人";
			HeadDot.IsVisible = false;
			PrivActions.IsVisible = false;
			GroupActions.IsVisible = true;
			QuitGroupButton.IsVisible = !isPublic;
			RenameGroupButton.IsVisible = !isPublic;
			GroupRemarkButton.IsVisible = !isPublic;
		}
	}

	// ---- 发送(桌面 ChatView.Input Send 同构:只发帧,消息经 ChatReceived 回来追加) ----

	void OnSendClicked(object? sender, EventArgs e) => SendText();

	void SendText()
	{
		var text = MsgEditor.Text?.Trim();
		if (string.IsNullOrEmpty(text))
			return;
		if (!AppState.Client.IsConnected) // 离线登录 / 断网退避期:不发,提示(不清输入,用户可复制/重发)
		{
			ShowStatus("没连接服务器");
			return;
		}
		// 消息经 ChatReceived 事件统一回来追加(本地回显),这里只发帧
		AppState.Client.SendChat(_convType, _convId, text);
		MsgEditor.Text = string.Empty;
		HideStatus(); // 发送成功清掉旧错误提示,避免误导
		HideKeyboard(); // 发送后收起输入法,方便看新气泡
	}

	// 状态条:错误 = 红底红字;success=true 时转绿底绿字(群备注保存结果,桌面 StatusText 位)
	void ShowStatus(string text, bool success = false)
	{
		StatusText.Text = text;
		StatusText.TextColor = success ? Color.FromArgb("#58B368") : Color.FromArgb("#E05B4C");
		StatusRow.BackgroundColor = success ? Color.FromArgb("#E5F6E0") : Color.FromArgb("#FCE8E2");
		StatusRow.IsVisible = true;
	}

	void HideStatus() => StatusRow.IsVisible = false;

	// ---- 头部动作:私聊(备注 → 服务器;删除好友 → 服务器,成功后关闭会话) ----

	// ✏ 备注:桌面 ChatView OnConvAction 同款文案/规则(备注不广播,本地快照先行,列表会话头即时生效)
	void OnRemarkClicked(object? sender, EventArgs e)
	{
		if (_isGroup) return;
		var friend = AppState.Client.LastSnapshot.Friends.FirstOrDefault(x => x.UserId == _convId);
		_promptAction = "remark";
		PromptUi.TitleText = "好友备注";
		PromptUi.HintText = "给好友起个备注(空 = 取消备注,最多 20 字)";
		PromptUi.PromptPlaceholder = "给 TA 起个新名字";
		PromptUi.InitialValue = friend?.Remark ?? "";
		OpenCard(PromptUi);
	}

	void OnDeleteFriendClicked(object? sender, EventArgs e)
	{
		if (_isGroup) return;
		_confirmAction = "delete";
		ConfirmUi.TitleText = "删除好友";
		ConfirmUi.MessageText = $"确定删除好友「{HeadTitle.Text}」吗?删除后双方好友列表都会移除,聊天记录保留。";
		ConfirmUi.ConfirmText = "删除";
		ConfirmUi.IsDanger = true;
		OpenCard(ConfirmUi);
	}

	async void DeleteFriend()
	{
		if (!AppState.Client.IsConnected)
		{
			ShowStatus("没连接服务器");
			return;
		}
		AppState.Client.SendDeleteFriend(_convId); // 服务器推 friendDeleted 给双方
		await CloseSelfAsync();                    // 会话没了,返回列表(与本端收到的 friendDeleted 去重)
	}

	// ---- 头部动作:群聊(成员页/退群 = 群主解散/改名与群备注提交服务器,回推 groupUpdated 刷新) ----

	void OnMembersClicked(object? sender, EventArgs e)
	{
		if (!_isGroup) return;
		_ = Shell.Current.GoToAsync($"members?groupId={_convId}");
	}

	// 🚪 退群:桌面 ChatView.OnLeaveGroup 同款文案逐字(群主也同一句话,退出即解散,解散由服务器广播)
	void OnQuitGroupClicked(object? sender, EventArgs e)
	{
		if (!_isGroup) return;
		var group = AppState.Client.LastSnapshot.Groups.FirstOrDefault(x => x.GroupId == _convId);
		if (group is null)
			return;
		_confirmAction = "quit";
		ConfirmUi.TitleText = "退出群聊";
		ConfirmUi.MessageText = $"确定退出群聊「{group.Name}」吗?\n(群主退出将解散该群,群聊记录一并删除)";
		ConfirmUi.ConfirmText = "退出";
		ConfirmUi.IsDanger = group.OwnerId == _myId;
		OpenCard(ConfirmUi);
	}

	async void QuitGroup()
	{
		if (!AppState.Client.IsConnected)
		{
			ShowStatus("没连接服务器");
			return;
		}
		AppState.Client.LeaveGroup(_convId); // 群主 = 解散(全员收 groupRemoved);成员 = 退出
		await CloseSelfAsync();              // 回列表(群移除由 groupRemoved 事件兜底,与之去重)
	}

	// ✏ 改名:桌面 ChatView OnConvAction 群分支同款(空名不改,>20 拦截;结果由 groupUpdated 回刷)
	void OnRenameGroupClicked(object? sender, EventArgs e)
	{
		if (!_isGroup) return;
		var group = AppState.Client.LastSnapshot.Groups.FirstOrDefault(x => x.GroupId == _convId);
		_promptAction = "rename";
		PromptUi.TitleText = "群聊改名";
		PromptUi.HintText = "输入新的群名称(最多 20 字)";
		PromptUi.PromptPlaceholder = "给群聊起个新名字";
		PromptUi.InitialValue = group?.Name ?? "";
		OpenCard(PromptUi);
	}

	// ✏ 备注:云端保存(公共群无此按钮);空 = 取消备注;回推会同步本账号其他连接
	void OnGroupRemarkClicked(object? sender, EventArgs e)
	{
		if (!_isGroup) return;
		_promptAction = "gremark";
		PromptUi.TitleText = "群备注";
		PromptUi.HintText = "给群起个备注(空 = 取消备注,最多 20 字)";
		PromptUi.PromptPlaceholder = "给群聊加个备注";
		PromptUi.InitialValue = AppState.Client.LastSnapshot.Settings
			.GetValueOrDefault("GroupRemark." + _convId) ?? "";
		OpenCard(PromptUi);
	}

	// ---- 弹层:输入卡(好友备注/群改名/群备注共用校验,桌面同款上限 20 字) ----

	void OnPromptAccepted(object? sender, EventArgs e)
	{
		var value = PromptUi.Value.Trim();
		if (_promptAction == "remark")
		{
			// 好友备注:空 = 取消备注(桌面同规则:SetFriendRemark 空值即移除;无回执,本地先行)
			if (value.Length > 20) { PromptUi.ShowError("备注最多 20 字"); return; }
			AppState.Client.SetFriendRemark(_convId, value); // 服务器落库
			var friend = AppState.Client.LastSnapshot.Friends.FirstOrDefault(x => x.UserId == _convId);
			if (friend is not null)
				friend.Remark = value; // 快照先行:会话头/列表下次刷新即新备注(回推无此字段)
			CloseOverlays();
			RefreshHeader();
		}
		else if (_promptAction == "rename")
		{
			if (value.Length == 0)
			{
				// 输入为空:不改名,群名保持原来的名字(桌面同文案)
				PromptUi.InitialValue = AppState.Client.LastSnapshot.Groups
					.FirstOrDefault(x => x.GroupId == _convId)?.Name ?? "";
				PromptUi.ShowError("群名不能为空,已保持原群名");
				return;
			}
			if (value.Length > 20) { PromptUi.ShowError("群名最多 20 字"); return; }
			var group = AppState.Client.LastSnapshot.Groups.FirstOrDefault(x => x.GroupId == _convId);
			if (group is not null)
				group.Name = value; // 快照先行(groupUpdated 回推后替换,幂等)
			AppState.Client.RenameGroup(_convId, value);
			CloseOverlays();
			RefreshHeader();
		}
		else if (_promptAction == "gremark")
		{
			// 群备注:云端保存(服务器落库并回推本账号全部连接);空 = 取消备注
			if (value.Length > 20) { PromptUi.ShowError("备注最多 20 字"); return; }
			if (!AppState.Client.IsConnected)
			{
				PromptUi.ShowError("没连接服务器");
				return;
			}
			AppState.Client.SetSetting("GroupRemark." + _convId, value);
			AppState.Client.LastSnapshot.Settings["GroupRemark." + _convId] = value; // 回推前本地先行
			CloseOverlays();
			RefreshHeader();
			ShowStatus(value.Length > 0 ? $"群备注已保存: {value} ✓" : "群备注已取消", success: true);
		}
	}

	// ---- 弹层:确认卡(删除好友/退群) ----

	void OnConfirmAccepted(object? sender, EventArgs e)
	{
		CloseOverlays();
		if (_confirmAction == "delete")
			DeleteFriend();
		else if (_confirmAction == "quit")
			QuitGroup();
	}

	void OnOverlayCanceled(object? sender, EventArgs e) => CloseOverlays();
	void OnOverlayDimTapped(object? sender, TappedEventArgs e) => CloseOverlays();

	// 气泡头像:私聊 → 对方好友详情页(备注/删除);群聊 → GetUserProfile 弹用户主页卡(可加好友)。
	// 桌面 ChatView.OnMsgAvatarClick/ShowUserProfile 同构(过期响应保护见 OnUserProfileReceived)
	void OnAvatarTapped(object? sender, TappedEventArgs e)
	{
		HideKeyboard();
		if (e.Parameter is not ChatMsgRow { Msg: { } m })
			return;
		if (_isGroup)
		{
			if (m.FromUserId == _myId)
				return; // 自己的消息在右侧无头像手势,兜底
			if (!AppState.Client.IsConnected) { ShowStatus("没连接服务器"); return; }
			_profilePendingId = m.FromUserId;
			AppState.Client.GetUserProfile(m.FromUserId); // 结果异步弹主页卡;不存在时服务器回 error
		}
		else
		{
			_ = Shell.Current.GoToAsync($"frienddetail?userId={_convId}");
		}
	}

	// 主页卡"＋ 添加好友":发申请,卡内按钮已先行转"已申请"置灰
	void OnProfileAddRequested(object? sender, EventArgs e)
	{
		if (!AppState.Client.IsConnected) { ShowStatus("没连接服务器"); return; }
		AppState.Client.SendFriendRequest(ProfileUi.TargetUserId, "");
	}

	// ---- emoji 面板(桌面 ChatView.Input 同款 50 个) ----

	private static readonly string[] Emojis =
	[
		"😀", "😄", "😁", "😂", "🤣", "😊", "😍", "🥰", "😘", "😎", "🤗", "🤔", "🙄", "😴", "🤤", "😭",
		"😅", "😉", "😜", "🤩", "🥳", "😇", "👍", "👏", "🙏", "💪", "🤝", "👌", "✌️", "❤️", "💕", "💖",
		"🎉", "🎂", "🍰", "☕", "🍺", "🎮", "🎯", "🏆", "🌹", "🌸", "🌈", "⭐", "🔥", "💯", "🐱", "🐶",
		"🐰", "🍀",
	];

	bool _emojiStateOn;
	Color _emojiBtnNormal = Color.FromArgb("#FFF0DC");
	Color _emojiBtnActive = Color.FromArgb("#FFE9D2");

	void OnEmojiClicked(object? sender, TappedEventArgs e) => ToggleEmojiPanel();

	void ToggleEmojiPanel()
	{
		_emojiStateOn = !_emojiStateOn;
		EmojiPanel.IsVisible = _emojiStateOn;
		EmojiBtnBg.BackgroundColor = _emojiStateOn ? _emojiBtnActive : _emojiBtnNormal;
		if (_emojiStateOn)
			HideKeyboard();   // 面板与输入法互斥,微信式
	}

	// 点选表情:插到光标处(收键盘后光标记录已落在 InsertEmoji 入参位置)
	void OnEmojiPicked(object? sender, TappedEventArgs e)
	{
		if (e.Parameter is not string emoji)
			return;
		var text = MsgEditor.Text ?? "";
		var pos = Math.Clamp(MsgEditor.CursorPosition, 0, text.Length);
		MsgEditor.Text = text.Insert(pos, emoji);
		MsgEditor.CursorPosition = pos + emoji.Length;
	}

	// 面板开着时点输入框:收起面板让位键盘
	void OnEditorFocused(object? sender, FocusEventArgs e)
	{
		if (EmojiPanel.IsVisible)
			ToggleEmojiPanel();
	}

	// ---- 刷新与滚动 ----

	/// <summary>滚到最新消息:无动画直达(动画滚动与列表位置调整叠加正是"来消息乱闪"的来源;与桌面 ScrollToEnd 同款)。</summary>
	void ScrollToEnd()
	{
		if (_rows.Count == 0)
			return;
		Dispatcher.Dispatch(() =>
			MsgList.ScrollTo(_rows.Count - 1, position: ScrollToPosition.End, animate: false));
	}

	/// <summary>日期栏文案:今天 / 昨天 / yyyy年M月d日 星期x(桌面逐字同款)。</summary>
	static string DateBarText(DateTime date)
	{
		var today = DateTime.Today;
		if (date == today)
			return "今天";
		if (date == today.AddDays(-1))
			return "昨天";
		var week = new[] { "日", "一", "二", "三", "四", "五", "六" }[(int)date.DayOfWeek];
		return $"{date.Year}年{date.Month}月{date.Day}日 星期{week}";
	}

	// ---- 弹层开关 / 收键盘 ----

	void OpenCard(View card)
	{
		CloseOverlays(keepOpened: card);
		Overlay.IsVisible = true;
		card.IsVisible = true;
		card.Opacity = 0;
		_ = card.FadeToAsync(1, 140, Easing.CubicOut);
		if (card is PromptCard prompt)
			prompt.FocusInput();
	}

	// keepOpened 传当前要打开的卡片(避免开卡瞬间把自身藏掉)
	void CloseOverlays(View? keepOpened = null)
	{
		if (!ReferenceEquals(PromptUi, keepOpened))
			PromptUi.IsVisible = false;
		if (!ReferenceEquals(ConfirmUi, keepOpened))
			ConfirmUi.IsVisible = false;
		if (!ReferenceEquals(ProfileUi, keepOpened))
			ProfileUi.IsVisible = false;
		if (keepOpened is null)
		{
			Overlay.IsVisible = false;
			HideKeyboard(); // C3:输入卡(备注/群名)关掉后输入法还常驻挡屏,这里一并收掉
		}
	}

	// 点根空白 / 消息行 / 滚动 / 手势区:收起输入法
	void OnRootTapped(object? sender, TappedEventArgs e) => HideKeyboard();
	void OnMsgRowTapped(object? sender, TappedEventArgs e) => HideKeyboard();
	void OnMsgListScrolled(object? sender, ItemsViewScrolledEventArgs e) => HideKeyboard();

	/// <summary>收起软键盘(共享实现见 Utils/KeyboardUtil)。</summary>
	static void HideKeyboard() => KeyboardUtil.HideKeyboard();
}
