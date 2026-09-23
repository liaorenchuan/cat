using QPet.Mobile.Models;
using QPet.Mobile.Services;
using QPet.Mobile.Utils;
using QPet.Mobile.Views.Controls;

namespace QPet.Mobile.Views;

/// <summary>
/// 消息页签(纯展示,数据/事件全由 MainPage 会话镜像驱动):
/// 连接横幅 + 我的资料卡(点击展开:改昵称 → 服务器提交、退出登录 → 回登录页)
/// + 会话列表(Reload 重建)。行点击进聊天页,左滑「关闭」= 本地隐藏会话(新消息自动恢复)。
/// </summary>
public partial class ConvPanel : ContentView
{
	public ConvPanel()
	{
		InitializeComponent();
	}

	/// <summary>重建会话列表(MainPage 每次镜像变动后调用)。</summary>
	public void Reload(List<ConvRow> rows) => ConvList.ItemsSource = rows;

	/// <summary>资料卡:昵称/账号/连接状态(登录后、改昵称后、连接变化时)。</summary>
	public void SetMyInfo(string nickname, string account, bool connected)
	{
		MyNickText.Text = nickname;
		MyAccountText.Text = $"账号 {account} · {(connected ? "在线" : "离线")}";
		MyAvatarChar.Text = nickname.Length > 0 ? nickname[..1] : "?";
		MyStatusDot.Fill = connected ? Color.FromArgb("#58B368") : Color.FromArgb("#C9C9C9");
	}

	/// <summary>连接状态横幅:在线隐藏;断线提示自动重连;离线登录模式 = 桌面同款提示。</summary>
	public void SetConnection(bool connected, bool offlineMode)
	{
		if (offlineMode)
		{
			ConnText.Text = "离线模式 · 未连接服务器,仅显示本地缓存聊天记录,消息不可发送";
			ConnText.TextColor = Color.FromArgb("#8A8278");
			ConnBanner.BackgroundColor = Color.FromArgb("#EFEBE3");
		}
		else if (!connected)
		{
			ConnText.Text = "连接已断开,正在自动重连…";
			ConnText.TextColor = Color.FromArgb("#C4702F");
			ConnBanner.BackgroundColor = Color.FromArgb("#FFF3E0");
		}
		ConnBanner.IsVisible = offlineMode || !connected;
	}

	// 行点击:带 会话标题 / 协议会话键(key=0:对方UserId 或 1:群Id)/ 是否群聊 推入聊天页
	void OnConvTapped(object? sender, TappedEventArgs e)
	{
		if (e.Parameter is not ConvRow conv)
			return;
		Shell.Current.GoToAsync(
			$"chat?title={Uri.EscapeDataString(conv.Title)}&key={conv.Key}&isGroup={conv.IsGroup}");
	}

	// 左滑「关闭」→ 本地隐藏会话(移除预览与未读,新消息来恢复);经 MainPage 落镜像并重建
	void OnCloseSwipe(object? sender, TappedEventArgs e)
	{
		if (e.Parameter is not ConvRow conv)
			return;
		if (Shell.Current.CurrentPage is MainPage main)
			main.CloseConvLocal(conv.Key);
	}

	// 资料卡点击:展开 / 收起 改昵称、退出登录 按钮区(箭头随状态翻转)
	void OnProfileTapped(object? sender, TappedEventArgs e)
	{
		var show = !MenuArea.IsVisible;
		MenuArea.IsVisible = show;
		MoreMark.Text = show ? "⌄" : "›";
		if (show)
		{
			MenuArea.Opacity = 0;
			_ = MenuArea.FadeToAsync(1, 150, Easing.CubicOut);
		}
	}

	// ---- 改昵称(提交服务器,服务器回推 profileUpdated 后 MainPage 再同步资料卡) ----

	void OnRenameClicked(object? sender, EventArgs e)
	{
		// 先收起展开区,避免输入卡底下露着两个按钮
		MenuArea.IsVisible = false;
		MoreMark.Text = "›";

		PromptUi.TitleText = "修改昵称";
		PromptUi.HintText = "改完资料卡、群成员里的「我」同步更新";
		PromptUi.PromptPlaceholder = "给自己起个新昵称";
		PromptUi.InitialValue = AppState.Client.MyUser.Nickname;
		Overlay.IsVisible = true;
		PromptUi.IsVisible = true;
		PromptUi.Opacity = 0;
		_ = PromptUi.FadeToAsync(1, 140, Easing.CubicOut);
		PromptUi.FocusInput();
	}

	void OnPromptAccepted(object? sender, EventArgs e)
	{
		var nick = PromptUi.Value;
		if (nick.Length == 0) { PromptUi.ShowError("昵称不能为空"); return; }
		if (nick.Length > 20) { PromptUi.ShowError("昵称最多 20 字"); return; }
		AppState.Client.UpdateNickname(nick);
		// 资料卡先行更新(服务器回推 profileUpdated 时 MainPage 会再同步一次,幂等)
		AppState.Client.MyUser.Nickname = nick;
		if (Shell.Current.CurrentPage is MainPage main)
			main.RefreshMyCard();
		ClosePrompt();
	}

	void ClosePrompt()
	{
		PromptUi.IsVisible = false;
		Overlay.IsVisible = false;
		KeyboardUtil.HideKeyboard(); // C3:改昵称输入卡关掉后输入法还常驻挡屏
	}

	void OnOverlayDimTapped(object? sender, TappedEventArgs e) => ClosePrompt();
	void OnPromptCanceled(object? sender, EventArgs e) => ClosePrompt();

	/// <summary>安卓返回键兜底:先收资料卡展开菜单,再收输入弹层。返回 true = 返回键已消费(不退出)。</summary>
	public bool ConsumeBack()
	{
		if (MenuArea.IsVisible)
		{
			MenuArea.IsVisible = false;
			MoreMark.Text = "›";
			return true;
		}
		if (Overlay.IsVisible)
		{
			ClosePrompt();
			return true;
		}
		return false;
	}

	// ---- 退出登录:落盘缓存、断开连接,弹回登录根(账号保留,下次预填) ----

	async void OnLogoutClicked(object? sender, EventArgs e)
	{
		AppState.Logout();
		await Shell.Current.GoToAsync("//login", animate: false);
	}
}
