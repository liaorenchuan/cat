using QPet.Mobile.Models;

namespace QPet.Mobile.Views;

/// <summary>
/// 群聊页签:2×2 动作行(创建 / 搜索 / 群申请 / 群邀请,后两枚有未处理显 ●)+ 群列表。
/// 列表数据由 MainPage 镜像驱动:Reload(rows) 随会话镜像各事件重建(群名/人数/备注实时);
/// 申请/邀请 ● 由 MainPage.RefreshBadges 同步(SetReqDot,与页签红点同源)。
/// 行点击 → 群详情(groupId 路由参数;详情页可改名/备注/看成员/退群)。
/// </summary>
public partial class GroupPanel : ContentView
{
	public GroupPanel()
	{
		InitializeComponent();
	}

	/// <summary>重建群列表(主界面每次镜像变动/回到主界面时调用)。</summary>
	public void Reload(List<GroupRow> rows)
	{
		GroupList.ItemsSource = rows;
	}

	/// <summary>待审批群申请 / 待同意群邀请 ●(有数才显;页签群红点同源)。</summary>
	public void SetReqDot(int apply, int invite)
	{
		ApplyDot.IsVisible = apply > 0;
		InviteDot.IsVisible = invite > 0;
	}

	// 行点击 → 群详情(groupId 路由参数;详情页可改名/看成员/退群)
	void OnGroupTapped(object? sender, TappedEventArgs e)
	{
		if (e.Parameter is not GroupRow row)
			return;
		Shell.Current.GoToAsync($"groupdetail?groupId={row.GroupId}");
	}

	async void OnCreateClicked(object? sender, EventArgs e) =>
		await Shell.Current.GoToAsync("creategroup");

	async void OnSearchClicked(object? sender, EventArgs e) =>
		await Shell.Current.GoToAsync("searchgroup");

	async void OnApplyClicked(object? sender, EventArgs e) =>
		await Shell.Current.GoToAsync("requests?kind=1");

	async void OnInviteClicked(object? sender, EventArgs e) =>
		await Shell.Current.GoToAsync("requests?kind=2");
}
