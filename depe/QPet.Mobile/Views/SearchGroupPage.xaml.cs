using QPet.Core;
using QPet.Mobile.Models;
using QPet.Mobile.Services;
using QPet.Mobile.Utils;

namespace QPet.Mobile.Views;

/// <summary>
/// 搜索加群页:SearchGroup 真实搜索(桌面 SearchGroupDialog 同款文案/逻辑)。
/// 输入防抖 250ms 后发请求;结果回 GroupSearchResultReceived(代际比对,迟到的旧回复丢弃)。
/// 行按钮按服务器状态四态:你创建的群/已在群内/已申请(灰)与公共群"＋ 加入"、好友群"✉ 申请";
/// 加入/申请后本地翻转结果行标志即刷新按钮态(入群/审批结果由 groupUpdated 推送驱动主列表)。
/// </summary>
public partial class SearchGroupPage : ContentPage
{
	private readonly List<GroupSearchRow> _rows = [];
	private int _searchGen; // 每次输入/清空递增:被取代的防抖与迟到回复一律作废
	private string _query = "";
	private bool _live;

	public SearchGroupPage()
	{
		InitializeComponent();
	}

	const string GuideCopy = "输入群名或群号搜索(群号在群聊列表和会话头部显示),点结果直接加入或申请。";

	protected override void OnAppearing()
	{
		base.OnAppearing();
		if (!_live)
		{
			_live = true;
			AppState.Client.GroupSearchResultReceived += OnGroupResults;
			AppState.Client.ErrorReceived += OnError;
		}
		// 返回本页时若搜索框还留着词,重建一次(结果来自服务器快照,不缓存)
		var query = SearchInput.Text?.Trim() ?? "";
		if (query.Length == 0)
			ShowHint(GuideCopy, ColorKind.Info);
		else
			AppState.Client.SearchGroup(query);
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (NavGuard.IsCovered(this))
			return; // 被覆盖:保持订阅(回来 OnAppearing 重建)
		if (!_live)
			return;
		_live = false;
		AppState.Client.GroupSearchResultReceived -= OnGroupResults;
		AppState.Client.ErrorReceived -= OnError;
	}

	async void OnCloseClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");

	async void OnSearchChanged(object? sender, TextChangedEventArgs e)
	{
		var query = e.NewTextValue?.Trim() ?? "";
		_query = query;
		var gen = ++_searchGen;
		if (query.Length == 0)
		{
			// 清空:回到引导文案,旧结果不保留(桌面清空搜索同)
			GroupList.ItemsSource = null;
			ShowHint(GuideCopy, ColorKind.Info);
			return;
		}
		ShowHint("搜索中...", ColorKind.Info);
		await Task.Delay(250); // 输入防抖:避免每敲一键发一次请求
		if (gen != _searchGen)
			return; // 输入已变,本次作废
		if (!AppState.Client.IsConnected)
		{
			ShowHint("没连接服务器", ColorKind.Error);
			return;
		}
		AppState.Client.SearchGroup(query); // 结果异步经 OnGroupResults
	}

	void OnGroupResults(List<GroupSearchResult> results)
	{
		// 结果到达前用户又搜了新词:旧回复丢弃(输入防抖也会等这代)
		if (SearchInput.Text?.Trim() != _query)
			return;
		_rows.Clear();
		foreach (var r in results)
			_rows.Add(new GroupSearchRow { R = r });
		GroupList.ItemsSource = null;
		GroupList.ItemsSource = _rows;
		ShowHint(results.Count > 0
			? $"找到 {results.Count} 个群,点右侧按钮加入或申请。"
			: $"没有找到与「{_query}」匹配的群", ColorKind.Info);
	}

	// 行按钮:公共群直接加入;好友群发申请。置灰态(你创建的群/已在群内/已申请)点不到,这里兜底
	void OnRowBtnClicked(object? sender, EventArgs e)
	{
		if (sender is not Button { BindingContext: GroupSearchRow row })
			return;
		var r = row.R;
		if (r.IsOwner || r.IsMember || r.HasPendingRequest)
			return;
		if (!AppState.Client.IsConnected)
		{
			ShowHint("没连接服务器", ColorKind.Error);
			return;
		}
		if (r.IsPublic)
		{
			AppState.Client.JoinGroup(r.GroupId);
			r.IsMember = true; // 本地翻转:按钮转"已在群内"(桌面同,成员资格已即时生效)
			ShowHint($"已加入「{r.Name}」,群聊已在左侧列表", ColorKind.Success);
		}
		else
		{
			AppState.Client.SendGroupRequest(r.GroupId);
			r.HasPendingRequest = true; // 本地翻转:按钮转"已申请"(审批结果推送前不再可点)
			ShowHint($"已向「{r.Name}」发送申请,群主/管理员审批后即可进群", ColorKind.Success);
		}
		GroupList.ItemsSource = null;
		GroupList.ItemsSource = _rows;
	}

	void OnError(string reason) => ShowHint(reason, ColorKind.Error);

	enum ColorKind { Info, Success, Error }

	void ShowHint(string text, ColorKind kind)
	{
		HintLabel.TextColor = kind == ColorKind.Error ? Color.FromArgb("#E05B4C")
			: kind == ColorKind.Success ? Color.FromArgb("#58B368")
			: Color.FromArgb("#B4A28F");
		HintLabel.Text = text;
		HintLabel.IsVisible = true;
	}
}
