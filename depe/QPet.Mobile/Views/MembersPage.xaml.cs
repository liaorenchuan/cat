using QPet.Core;
using QPet.Mobile.Models;
using QPet.Mobile.Services;
using QPet.Mobile.Utils;
using QPet.Mobile.Views.Controls;

namespace QPet.Mobile.Views;

/// <summary>
/// 群成员页:members?groupId=…(桌面 MembersDialog 的移动端对应;群头"👥 成员"/群详情成员行进)。
/// 成员 = 主界面共享群快照(排序:群主置顶 → 管理员 → 普通成员),行尾按查看者视角给操作:
/// ＋ 好友(非我非好友,发后置"已发送")/ 设为·取消管理(仅群主,乐观翻转)/ 🚫 移出(群主移任何人
/// 除自己/群主,管理员移普通成员;确认后乐观移除);底部拉好友进群 = 邀请制(桌面同:点"拉入"发
/// 邀请,对方同意后 groupUpdated 推送自动入群,这里实时刷新;失败(非好友等)恢复按钮并提示)。
/// 服务器错误统一提示到底部状态行(桌面 InviteHintText 位)。
/// </summary>
public partial class MembersPage : ContentPage, IQueryAttributable
{
	private string _groupId = "";
	private GroupInfo? _group;
	private readonly List<GroupMemberRow> _rows = [];
	private readonly List<InviteOptionRow> _inviteRows = [];
	private GroupMemberRow? _kickRow;      // 移出确认的目标行(确认卡事件不带行上下文)
	private string _lastInviteUserId = ""; // 最近一次拉人目标(失败时恢复其按钮)
	private bool _live;
	private bool _leaving;

	public MembersPage()
	{
		InitializeComponent();
		MemberList.ItemsSource = _rows;
		InviteList.ItemsSource = _inviteRows;
	}

	public void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		_groupId = query.TryGetValue("groupId", out var v) ? v.ToString() ?? "" : "";
		CloseOverlays();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		if (!_live)
		{
			_live = true;
			var c = AppState.Client;
			c.GroupUpdated += OnGroupUpdated;   // 成员变动(改名/加人/设管理):整体重建
			c.ErrorReceived += OnError;         // 拉人失败(不是好友等)提示并恢复
			c.GroupRemoved += OnGroupRemoved;   // 群解散/我被移出:关页
		}
		RefreshAll();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (NavGuard.IsCovered(this))
			return; // 被覆盖:保持订阅
		if (!_live)
			return;
		_live = false;
		var c = AppState.Client;
		c.GroupUpdated -= OnGroupUpdated;
		c.ErrorReceived -= OnError;
		c.GroupRemoved -= OnGroupRemoved;
	}

	void OnGroupUpdated(GroupInfo group)
	{
		if (_live && group.GroupId == _groupId)
			RefreshAll(); // 成员/管理员变动实时刷新(含被邀者刚同意入群)
	}

	void OnGroupRemoved(string groupId, string reason)
	{
		if (_live && !_leaving && groupId == _groupId)
		{
			_leaving = true;
			_ = Shell.Current.GoToAsync("..");
		}
	}

	// ---- 数据:共享群快照整体重建(成员 + 可拉好友 + 标题人数) ----

	void RefreshAll()
	{
		var snap = AppState.Client.LastSnapshot;
		_group = snap.Groups.FirstOrDefault(x => x.GroupId == _groupId);
		if (_group is null)
		{
			// 群已解散/被移出(事件通常已先关页,这里兜底)
			if (!_leaving)
			{
				_leaving = true;
				_ = Shell.Current.GoToAsync("..");
			}
			return;
		}
		Sheet.Title = $"{_group.Name} · 共 {_group.Members.Count} 人";
		HideInviteTip();
		RebindMembers();
		RebindInvites();
	}

	void RebindMembers()
	{
		var group = _group;
		if (group is null)
			return;
		var snap = AppState.Client.LastSnapshot;
		var meId = AppState.Client.MyUser.UserId;
		var friendIds = snap.Friends.Select(f => f.UserId).ToHashSet();
		var isOwner = group.OwnerId == meId;
		var isAdmin = group.AdminIds.Contains(meId);
		_rows.Clear();
		// 排序:群主置顶 → 管理员 → 普通成员(同权重保持原顺序;桌面 MembersDialog 同构)
		foreach (var m in group.Members.OrderBy(m => m.UserId == group.OwnerId ? 0
			: group.AdminIds.Contains(m.UserId) ? 1 : 2))
		{
			_rows.Add(new GroupMemberRow
			{
				UserId = m.UserId,
				Nickname = m.Nickname,
				Account = m.Account,
				Online = m.Online,
				IsMe = m.UserId == meId,
				IsFriend = friendIds.Contains(m.UserId),
				IsOwnerRole = isOwner,
				IsAdminRole = isAdmin,
				OwnerId = group.OwnerId,
				IsAdmin = group.AdminIds.Contains(m.UserId),
			});
		}
		MemberList.ItemsSource = null;
		MemberList.ItemsSource = _rows;
	}

	void RebindInvites()
	{
		var group = _group;
		if (group is null)
			return;
		var snap = AppState.Client.LastSnapshot;
		// 可拉入的好友 = 我的好友 - 已在群内的(桌面同规则,名称备注优先)
		var inGroup = group.Members.Select(m => m.UserId).ToHashSet();
		_inviteRows.Clear();
		foreach (var f in snap.Friends)
		{
			if (inGroup.Contains(f.UserId))
				continue;
			_inviteRows.Add(new InviteOptionRow
			{
				UserId = f.UserId,
				Title = f.Remark.Length > 0 ? f.Remark : f.Nickname,
			});
		}
		InviteList.ItemsSource = null;
		InviteList.ItemsSource = _inviteRows;
		InviteCount.Text = _inviteRows.Count > 0 ? $"可拉 {_inviteRows.Count} 人" : "";
		InviteEmptyText.Text = snap.Friends.Count == 0
			? "还没有好友,先去好友页加几个人吧"
			: "没有可拉入的好友,好友都在这个群里了";
	}

	// ---- 行尾操作 ----

	// ＋ 好友:发申请,行内按钮置"已发送"(服务器去重,重复点无害;桌面 OnMemberAdd 同构)
	void OnAddClicked(object? sender, EventArgs e)
	{
		if (sender is not Button { BindingContext: GroupMemberRow row })
			return;
		AppState.Client.SendFriendRequest(row.UserId, "");
		row.MarkAddSent();
		MemberList.ItemsSource = null;
		MemberList.ItemsSource = _rows;
	}

	// 设为/取消管理(仅群主视角):发送后本地翻转按钮,服务器广播 groupUpdated 整体重建
	void OnToggleAdminClicked(object? sender, EventArgs e)
	{
		if (sender is not Button { BindingContext: GroupMemberRow row })
			return;
		row.IsAdmin = !row.IsAdmin;
		AppState.Client.SetGroupAdmin(_groupId, row.UserId, row.IsAdmin);
		MemberList.ItemsSource = null;
		MemberList.ItemsSource = _rows;
	}

	// 🚫 移出成员(群主/管理员权限行才有按钮):确认后发送 + 本地乐观移除(桌面 OnKickMember 同构)
	void OnKickClicked(object? sender, EventArgs e)
	{
		if (sender is not Button { BindingContext: GroupMemberRow row })
			return;
		_kickRow = row;
		ConfirmUi.TitleText = "移出成员";
		ConfirmUi.MessageText = $"把「{row.Nickname}」移出群聊?";
		ConfirmUi.ConfirmText = "移出";
		ConfirmUi.IsDanger = true;
		OpenCard(ConfirmUi);
	}

	void OnConfirmAccepted(object? sender, EventArgs e)
	{
		CloseOverlays();
		var row = _kickRow;
		_kickRow = null;
		if (row is null)
			return;
		if (!AppState.Client.IsConnected)
		{
			ShowInviteTip("没连接服务器", error: true);
			return;
		}
		AppState.Client.RemoveGroupMember(_groupId, row.UserId);
		// 乐观移除:服务器广播 groupUpdated 时整体重建;这里先移除防残留(桌面同)
		_rows.RemoveAll(x => x.UserId == row.UserId);
		MemberList.ItemsSource = null;
		MemberList.ItemsSource = _rows;
		if (_group is not null)
			Sheet.Title = $"{_group.Name} · 共 {_rows.Count} 人";
	}

	// ---- 拉好友进群(邀请制:点"拉入"发邀请,对方同意才入群) ----

	void OnInviteClicked(object? sender, EventArgs e)
	{
		if (sender is not Button { BindingContext: InviteOptionRow row } || row.Sent)
			return;
		if (!AppState.Client.IsConnected)
		{
			ShowInviteTip("没连接服务器", error: true);
			return;
		}
		row.Sent = true; // 防重复点击;失败时 OnError 恢复并提示(桌面同)
		_lastInviteUserId = row.UserId;
		AppState.Client.SendGroupInvite(_groupId, row.UserId);
		InviteList.ItemsSource = null;
		InviteList.ItemsSource = _inviteRows;
		ShowInviteTip("已发出邀请,等待对方同意", error: false);
	}

	/// <summary>服务器错误(拉人失败等):恢复最近一次邀请按钮,提示原因(桌面 OnError 同构)。</summary>
	void OnError(string reason)
	{
		if (_lastInviteUserId.Length > 0)
		{
			var row = _inviteRows.FirstOrDefault(x => x.UserId == _lastInviteUserId);
			if (row is not null)
			{
				row.Sent = false;
				InviteList.ItemsSource = null;
				InviteList.ItemsSource = _inviteRows;
			}
			_lastInviteUserId = "";
		}
		ShowInviteTip(reason, error: true);
	}

	void ShowInviteTip(string text, bool error)
	{
		InviteTip.Text = text;
		InviteTip.TextColor = error ? Color.FromArgb("#E05B4C") : Color.FromArgb("#58B368");
		InviteTip.IsVisible = true;
	}

	void HideInviteTip()
	{
		InviteTip.Text = "";
		InviteTip.IsVisible = false;
	}

	async void OnCloseClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");

	// ---- 弹层开关 ----

	void OpenCard(View card)
	{
		Overlay.IsVisible = true;
		card.IsVisible = true;
		card.Opacity = 0;
		_ = card.FadeToAsync(1, 140, Easing.CubicOut);
	}

	void CloseOverlays()
	{
		ConfirmUi.IsVisible = false;
		Overlay.IsVisible = false;
		KeyboardUtil.HideKeyboard(); // C3:确认卡关掉后输入法还常驻挡屏(本页有搜索框)
	}

	void OnOverlayDimTapped(object? sender, TappedEventArgs e) => CloseOverlays();
	void OnOverlayCanceled(object? sender, EventArgs e) => CloseOverlays();

	protected override bool OnBackButtonPressed()
	{
		if (Overlay.IsVisible) { CloseOverlays(); return true; }
		return base.OnBackButtonPressed();
	}
}
