using QPet.Core;
using QPet.Mobile.Models;
using QPet.Mobile.Services;
using QPet.Mobile.Utils;

namespace QPet.Mobile.Views;

/// <summary>
/// 搜索好友页(桌面 AddFriendDialog 的移动端对应):输入即请求-响应搜索,
/// 状态文案对齐桌面:搜索中... / 找到 N 个用户 / 没有找到匹配的用户 / 已向 X 发送申请,等待对方同意;
/// 结果按钮:已是好友 / 已申请 / ＋ 添加。空输入 = 清结果 + 空态引导文案。
/// 发送失败(重复申请/已是好友/对方禁用等)由服务器 error 帧回滚按钮并显示失败原因(不再假成功)。
/// </summary>
public partial class AddFriendPage : ContentPage
{
	private readonly List<UserSearchRow> _rows = [];
	private string _query = "";
	private string _sentToUserId = ""; // 刚发过申请的对方 ID(error 帧据此判定是否本次发送失败)
	private bool _live;

	public AddFriendPage()
	{
		InitializeComponent();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		if (_live)
			return;
		_live = true;
		AppState.Client.SearchResultsReceived += OnSearchResults;
		AppState.Client.ErrorReceived += OnError;
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (NavGuard.IsCovered(this))
			return; // 被覆盖:保持订阅(本页不推子页,保险)
		if (!_live)
			return;
		_live = false;
		AppState.Client.SearchResultsReceived -= OnSearchResults;
		AppState.Client.ErrorReceived -= OnError;
	}

	async void OnCloseClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");

	// 输入即搜索(桌面同款,无防抖);空输入清结果回到引导态
	void OnSearchChanged(object? sender, TextChangedEventArgs e)
	{
		var query = SearchInput.Text?.Trim() ?? "";
		_query = query;
		if (query.Length == 0)
		{
			RowsClear();
			HideHint();
			EmptyLabel.Text = "输入对方账号或昵称搜索,然后发送好友申请。";
			return;
		}
		if (!AppState.Client.IsConnected || AppState.Client.IsOffline)
		{
			RowsClear();
			EmptyLabel.Text = "";
			ShowHint("没连接服务器");
			return;
		}
		RowsClear();
		EmptyLabel.Text = ""; // 结果区只留图标,状态看提示行
		ShowHint("搜索中...");
		AppState.Client.SearchFriends(query);
	}

	void RowsClear()
	{
		_rows.Clear();
		ResultList.ItemsSource = null;
	}

	void ShowHint(string text, bool error = false)
	{
		HintText.TextColor = error ? Color.FromArgb("#E05B4C") : Color.FromArgb("#B4A28F");
		HintText.Text = text;
		HintText.IsVisible = true;
	}

	void HideHint()
	{
		HintText.Text = "";
		HintText.IsVisible = false;
	}

	// 发送申请失败(如已是好友/重复申请/对方被禁):回滚该行按钮态并显示失败原因(桌面 error 同文案)
	void OnError(string reason)
	{
		if (_sentToUserId.Length == 0)
			return;
		var row = _rows.FirstOrDefault(r => r.UserId == _sentToUserId);
		_sentToUserId = "";
		if (row is null)
			return;
		row.Sent = false; // 请求没成功:按钮回到可点,文案同步更新
		ResultList.ItemsSource = null;
		ResultList.ItemsSource = _rows;
		ShowHint(reason, error: true);
	}

	void OnSearchResults(List<UserSearchResult> results)
	{
		if (_query.Length == 0)
			return; // 输入已清空:过期响应直接丢
		_rows.Clear();
		foreach (var r in results)
			_rows.Add(new UserSearchRow
			{
				UserId = r.UserId,
				Nickname = r.Nickname,
				Account = r.Account,
				IsFriend = r.IsFriend,
				Requested = r.HasPendingRequest,
			});
		ResultList.ItemsSource = null;
		ResultList.ItemsSource = _rows;
		ShowHint(results.Count == 0 ? "没有找到匹配的用户" : $"找到 {results.Count} 个用户");
	}

	// ＋ 添加 → 发申请(留言空),按钮即时置"已申请"(服务器状态下一轮搜索自会带上);
	// 失败时 error 帧经 OnError 回滚(不会停留在假"已申请")
	void OnAddClicked(object? sender, EventArgs e)
	{
		if (sender is not Button { BindingContext: UserSearchRow row } || !row.BtnEnabled)
			return;
		AppState.Client.SendFriendRequest(row.UserId, "");
		_sentToUserId = row.UserId; // 记住本次发送:error 帧据此回滚
		row.MarkSent();
		ResultList.ItemsSource = null;
		ResultList.ItemsSource = _rows;
		ShowHint($"已向 {row.Nickname} 发送申请,等待对方同意");
	}
}
