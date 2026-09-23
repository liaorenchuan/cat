using QPet.Mobile.Models;

namespace QPet.Mobile.Views;

/// <summary>
/// 好友页签:搜索/申请按钮行(申请有未处理显 ●)+ 好友列表(备注优先,行点击进详情)。
/// 列表数据由 MainPage 镜像驱动:Reload(rows) 随会话镜像各事件重建;
/// 申请 ● 由 MainPage.RefreshBadges 同步(SetReqDot,与页签红点同源)。
/// </summary>
public partial class FriendPanel : ContentView
{
	public FriendPanel()
	{
		InitializeComponent();
	}

	/// <summary>重建好友列表(主界面每次镜像变动/回到主界面时调用)。</summary>
	public void Reload(List<FriendRow> rows)
	{
		FriendList.ItemsSource = rows;
	}

	/// <summary>待处理好友申请 ●(有数才显;页签好友红点同源)。</summary>
	public void SetReqDot(int pending)
	{
		ReqDot.IsVisible = pending > 0;
	}

	// 行点击 → 好友详情(userId 路由参数;详情页可改备注/发消息/删除)
	void OnFriendTapped(object? sender, TappedEventArgs e)
	{
		if (e.Parameter is not FriendRow row)
			return;
		Shell.Current.GoToAsync($"frienddetail?userId={row.UserId}");
	}

	async void OnSearchClicked(object? sender, EventArgs e) =>
		await Shell.Current.GoToAsync("addfriend");

	async void OnRequestsClicked(object? sender, EventArgs e) =>
		await Shell.Current.GoToAsync("requests?kind=0");
}
