using QPet.Core;
using QPet.Mobile.Models;
using QPet.Mobile.Services;
using QPet.Mobile.Utils;

namespace QPet.Mobile.Views;

/// <summary>
/// 申请中心:好友申请 / 群申请 / 群邀请三个页签(kind=0..2 路由进入;桌面 RequestsDialog 的移动端对应)。
/// 数据源 = SyncClient 最近一次 welcome 快照 + Received 事件实时增补 / Handled 移除(桌面同构);
/// 同意/拒绝即发帧并本地移除;服务器向审批人回推 handled 帧,主界面红点由它递减(桌面同款,不自减);
/// 每行按钮的 BindingContext 即行模型,三页签共用同意/拒绝入口按 _kind 分发。
/// </summary>
public partial class RequestsPage : ContentPage, IQueryAttributable
{
	private int _kind;
	private bool _live;
	private readonly List<FriendReqRow> _friendRows = [];
	private readonly List<GroupReqRow> _groupRows = [];
	private readonly List<InviteReqRow> _inviteRows = [];

	public RequestsPage()
	{
		InitializeComponent();
	}

	public void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		_kind = query.TryGetValue("kind", out var k)
			&& int.TryParse(k.ToString(), out var v) ? v : 0;
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		if (!_live)
		{
			_live = true;
			var c = AppState.Client;
			c.FriendRequestReceived += OnRequestReceived;
			c.RequestHandled += OnRequestHandled;
			c.GroupRequestReceived += OnGroupRequestReceived;
			c.GroupRequestHandled += OnGroupRequestHandled;
			c.GroupInviteReceived += OnGroupInviteReceived;
			c.GroupInviteHandled += OnGroupInviteHandled;
			c.WelcomeReceived += OnWelcome; // 登录后立刻点开:快照可能未到,welcome 到了整体刷一次
		}
		SelectTab(_kind);
		RefreshAll();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (NavGuard.IsCovered(this))
			return; // 被其他页覆盖:保持订阅(实时增补不掉)
		if (!_live)
			return;
		_live = false;
		var c = AppState.Client;
		c.FriendRequestReceived -= OnRequestReceived;
		c.RequestHandled -= OnRequestHandled;
		c.GroupRequestReceived -= OnGroupRequestReceived;
		c.GroupRequestHandled -= OnGroupRequestHandled;
		c.GroupInviteReceived -= OnGroupInviteReceived;
		c.GroupInviteHandled -= OnGroupInviteHandled;
		c.WelcomeReceived -= OnWelcome;
	}

	async void OnCloseClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");

	void OnTabFriendClicked(object? sender, EventArgs e) => SelectTab(0);
	void OnTabGroupClicked(object? sender, EventArgs e) => SelectTab(1);
	void OnTabInviteClicked(object? sender, EventArgs e) => SelectTab(2);

	// 切页签:只显对应列表,胶囊高亮(与主界面页签同视觉语言)
	void SelectTab(int kind)
	{
		_kind = kind;
		FriendReqList.IsVisible = kind == 0;
		GroupReqList.IsVisible = kind == 1;
		InviteList.IsVisible = kind == 2;
		SetTabStyle(TabFriendReq, kind == 0);
		SetTabStyle(TabGroupReq, kind == 1);
		SetTabStyle(TabInvite, kind == 2);
	}

	static void SetTabStyle(Button b, bool selected)
	{
		b.BackgroundColor = selected ? Color.FromArgb("#FFE9D2") : Colors.Transparent;
		b.TextColor = selected ? Color.FromArgb("#C4702F") : Color.FromArgb("#B08A62");
		b.FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None;
	}

	// ---- 数据:welcome 全量重建 / 事件增量(桌面 RequestsDialog 各 handler 同构) ----

	void OnWelcome(AuthSnapshot snap) => RefreshAll();

	void RefreshAll()
	{
		var snap = AppState.Client.LastSnapshot;
		_friendRows.Clear();
		foreach (var r in snap.Requests)
			_friendRows.Add(new FriendReqRow { Req = r });
		_groupRows.Clear();
		foreach (var r in snap.GroupRequests)
			_groupRows.Add(new GroupReqRow { Req = r });
		_inviteRows.Clear();
		foreach (var r in snap.GroupInvites)
			_inviteRows.Add(new InviteReqRow { Inv = r });
		Bind(0);
		Bind(1);
		Bind(2);
	}

	void OnRequestReceived(FriendRequest req)
	{
		if (_friendRows.Any(x => x.Req.ReqId == req.ReqId))
			return;
		_friendRows.Insert(0, new FriendReqRow { Req = req });
		Bind(0);
	}

	/// <summary>我的申请被处理(对方同意/拒绝):从列表移除(重开也不会再出现)。</summary>
	void OnRequestHandled(long requestId)
	{
		if (_friendRows.RemoveAll(x => x.Req.ReqId == requestId) == 0)
			return;
		Bind(0);
	}

	void OnGroupRequestReceived(GroupRequest req)
	{
		if (_groupRows.Any(x => x.Req.ReqId == req.ReqId))
			return;
		_groupRows.Insert(0, new GroupReqRow { Req = req });
		Bind(1);
	}

	void OnGroupRequestHandled(long requestId)
	{
		if (_groupRows.RemoveAll(x => x.Req.ReqId == requestId) == 0)
			return;
		Bind(1);
	}

	void OnGroupInviteReceived(GroupInvite invite)
	{
		if (_inviteRows.Any(x => x.Inv.InviteId == invite.InviteId))
			return;
		_inviteRows.Insert(0, new InviteReqRow { Inv = invite });
		Bind(2);
	}

	void OnGroupInviteHandled(long inviteId)
	{
		if (_inviteRows.RemoveAll(x => x.Inv.InviteId == inviteId) == 0)
			return;
		Bind(2);
	}

	// ---- 同意 / 拒绝(本地移除 + 快照移除;主界面徽章由服务器 handled 回推递减,与桌面同款) ----

	void OnAgreeClicked(object? sender, EventArgs e) => Respond(sender, true);
	void OnRefuseClicked(object? sender, EventArgs e) => Respond(sender, false);

	void Respond(object? sender, bool accept)
	{
		if (sender is not Button { BindingContext: not null } btn)
			return;
		switch (_kind)
		{
			case 0 when btn.BindingContext is FriendReqRow f:
				AppState.Client.RespondFriendRequest(f.Req.ReqId, accept);
				// 同意后双方由 friendAdded 推送(列表自动合并);本地移除该项(handled 回执幂等)
				AppState.Client.LastSnapshot.Requests.RemoveAll(x => x.ReqId == f.Req.ReqId);
				_friendRows.Remove(f);
				Bind(0);
				break;
			case 1 when btn.BindingContext is GroupReqRow g:
				AppState.Client.RespondGroupRequest(g.Req.ReqId, accept);
				// 同意后申请人由 groupUpdated 推送自动入群;本地先移除(审批成功即已处理)
				AppState.Client.LastSnapshot.GroupRequests.RemoveAll(x => x.ReqId == g.Req.ReqId);
				_groupRows.Remove(g);
				Bind(1);
				break;
			case 2 when btn.BindingContext is InviteReqRow i:
				AppState.Client.RespondGroupInvite(i.Inv.InviteId, accept);
				// 同意后入群由 groupUpdated 推送自动刷新;本地先移除(已处理)
				AppState.Client.LastSnapshot.GroupInvites.RemoveAll(x => x.InviteId == i.Inv.InviteId);
				_inviteRows.Remove(i);
				Bind(2);
				break;
		}
	}

	// ---- 列表重建 + 页签计数(主界面红点同源,这里仅展示) ----

	void Bind(int kind)
	{
		switch (kind)
		{
			case 0:
				FriendReqList.ItemsSource = null;
				FriendReqList.ItemsSource = _friendRows;
				break;
			case 1:
				GroupReqList.ItemsSource = null;
				GroupReqList.ItemsSource = _groupRows;
				break;
			case 2:
				InviteList.ItemsSource = null;
				InviteList.ItemsSource = _inviteRows;
				break;
		}
		TabFriendReq.Text = TabText("好友申请", _friendRows.Count);
		TabGroupReq.Text = TabText("群申请", _groupRows.Count);
		TabInvite.Text = TabText("群邀请", _inviteRows.Count);
	}

	static string TabText(string name, int n) => n > 0 ? $"{name} ({(n > 99 ? "99+" : n.ToString())})" : name;
}
