using QPet.Mobile.Services;
using QPet.Mobile.Views;

namespace QPet.Mobile;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();

		// 路由注册:
		//   main   = 主界面,登录/注册成功后 push(登录页 login 是 ShellContent 根)
		//   chat   = 聊天详情页,携带 title/key/isGroup 路由参数(会话列表行点击)
		//   members= 群成员页(携带 num 群号;聊天页 👥 成员 按钮进入)
		//   addfriend / frienddetail / requests
		//          = 好友链路:搜索加好友 / 好友详情(account) / 申请中心(kind 0..2 页签)
		//   groupdetail / searchgroup / creategroup
		//          = 群链路:群详情(num)/ 搜索加群 / 创建群(好友多选)
		Routing.RegisterRoute("main", typeof(MainPage));
		Routing.RegisterRoute("chat", typeof(ChatPage));
		Routing.RegisterRoute("members", typeof(MembersPage));
		Routing.RegisterRoute("addfriend", typeof(AddFriendPage));
		Routing.RegisterRoute("frienddetail", typeof(FriendDetailPage));
		Routing.RegisterRoute("requests", typeof(RequestsPage));
		Routing.RegisterRoute("groupdetail", typeof(GroupDetailPage));
		Routing.RegisterRoute("searchgroup", typeof(SearchGroupPage));
		Routing.RegisterRoute("creategroup", typeof(CreateGroupPage));

		// 被服务器强制下线(禁用/删除/凭据失效):先记下原因再弹回登录根,登录页 OnAppearing 显示
		AppState.Kicked += OnKicked;
	}

	async void OnKicked(string reason)
	{
		LoginPage.PendingKickReason = reason;
		await GoToAsync("//login"); // 弹空 main 及其上页面栈,回登录根
	}
}
