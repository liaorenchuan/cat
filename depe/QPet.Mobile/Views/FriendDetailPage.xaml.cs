using QPet.Core;
using QPet.Mobile.Models;
using QPet.Mobile.Services;
using QPet.Mobile.Utils;
using QPet.Mobile.Views.Controls;

namespace QPet.Mobile.Views;

/// <summary>
/// 好友详情页:frienddetail?userId=…(桌面 FriendDetailDialog 的移动端对应)。
/// 账号经 GetUserProfile 拉取(同时是"好友已被删"检测);备注规则与桌面一致:
/// 空 = 清除(服务器空值即移除),>20 提示"备注最多 20 字",保存后本地快照先行 + "备注已保存 ✓";
/// 删除确认文案与桌面逐字一致。在线点/备注数据 = 主界面共享快照(各处改动即时反映)。
/// </summary>
public partial class FriendDetailPage : ContentPage, IQueryAttributable
{
	private string _userId = "";
	private FriendInfo? _friend;
	private string _account = "";   // 资料拉取成功后缓存(从聊天页回来不重复拉)
	private bool _profileFetched;   // 资料只拉一次(离线登录/拉取失败不重试,镜像账号可兜底)
	private bool _profileLoaded;    // 拉取完成前收到的 error 都提示(对方可能已把我删了)
	private bool _live;

	public FriendDetailPage()
	{
		InitializeComponent();
	}

	public void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		_userId = query.TryGetValue("userId", out var v) ? v.ToString() ?? "" : "";
		_friend = null;
		_account = "";
		_profileFetched = false;
		_profileLoaded = false;
		CloseOverlays();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		if (!_live)
		{
			_live = true;
			AppState.Client.UserProfileReceived += OnUserProfileReceived;
			AppState.Client.ErrorReceived += OnError;
		}
		Reload();
		if (!_profileFetched && AppState.Client.IsConnected && !AppState.Client.IsOffline)
		{
			_profileFetched = true; // 账号/最新昵称是公开资料,异步拉取(也是新人检测)
			AppState.Client.GetUserProfile(_userId);
		}
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (NavGuard.IsCovered(this))
			return; // 被聊天页等覆盖:保持订阅(回来 Reload 取最新镜像)
		if (!_live)
			return;
		_live = false;
		AppState.Client.UserProfileReceived -= OnUserProfileReceived;
		AppState.Client.ErrorReceived -= OnError;
	}

	// 每次出现重读镜像(备注可能刚从聊天页改过,在线点随时在变)
	void Reload()
	{
		_friend = AppState.Client.LastSnapshot.Friends.FirstOrDefault(x => x.UserId == _userId);
		HideTip();
		UpdateCard();
	}

	void UpdateCard()
	{
		var f = _friend;
		var name = DisplayName();
		NameText.Text = name;
		var hasRemark = f is not null && f.Remark.Length > 0;
		OriginNickText.IsVisible = hasRemark;
		if (hasRemark)
			OriginNickText.Text = $"原昵称 {f!.Nickname}";
		AvatarCharText.Text = name.Length > 0 ? name[..1] : "?";
		var connected = AppState.Client.IsConnected && !AppState.Client.IsOffline;
		var on = connected && f?.Online == true;
		StatusDot.Fill = on ? Color.FromArgb("#58B368") : Color.FromArgb("#C9C9C9");
		var remark = f is not null ? f.Remark : "";
		RemarkText.Text = remark.Length > 0 ? remark : "未设置";
		RemarkText.TextColor = remark.Length > 0
			? Color.FromArgb("#C4702F") : Color.FromArgb("#B4A28F");
		AccountText.Text = _account.Length > 0 ? _account
			: f is { Account.Length: > 0 } ? f.Account : "";
		StatusText.Text = on ? "在线" : "离线";
		StatusText.TextColor = on ? Color.FromArgb("#58B368") : Color.FromArgb("#B4A28F");
	}

	/// <summary>显示名:备注优先,其次镜像昵称,再退回上一次显示值。</summary>
	string DisplayName()
	{
		var f = _friend;
		if (f is not null && (f.Remark.Length > 0 || f.Nickname.Length > 0))
			return f.Remark.Length > 0 ? f.Remark : f.Nickname;
		return NameText.Text.Length > 0 ? NameText.Text : "好友";
	}

	void OnUserProfileReceived(UserProfile profile)
	{
		if (profile.UserId != _userId)
			return; // 过期响应(详情页快速切换)
		_profileLoaded = true;
		_account = profile.Account;
		AccountText.Text = _account;
		// 无备注时跟随服务器最新昵称(好友刚改名也能追上)
		if (_friend is not null && _friend.Remark.Length == 0 && profile.Nickname.Length > 0)
		{
			_friend.Nickname = profile.Nickname;
			NameText.Text = profile.Nickname;
			AvatarCharText.Text = profile.Nickname[..1];
		}
	}

	void OnError(string reason)
	{
		// 好友已被删/资料拉取失败:提示但可继续操作(发消息/删除会由服务器再判)
		if (!_profileLoaded)
			ShowTip(reason, error: true);
	}

	async void OnCloseClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");

	// 备注行 → 输入卡(桌面规则:空 = 清除;仅 >20 拦截)
	void OnRemarkRowTapped(object? sender, TappedEventArgs e)
	{
		PromptUi.TitleText = "修改备注";
		PromptUi.HintText = "设置后,好友列表与私聊会话都会显示这个备注名";
		PromptUi.PromptPlaceholder = "给 TA 起个新名字";
		PromptUi.InitialValue = _friend?.Remark ?? "";
		OpenCard(PromptUi);
	}

	void OnPromptAccepted(object? sender, EventArgs e)
	{
		var value = PromptUi.Value.Trim();
		if (value.Length > 20) { PromptUi.ShowError("备注最多 20 字"); return; }
		AppState.Client.SetFriendRemark(_userId, value); // 服务器落库(空 = 清除备注)
		if (_friend is not null)
			_friend.Remark = value; // 快照共享实例本地先行:好友列表/会话标题下次刷新即新备注
		CloseOverlays();
		UpdateCard();
		ShowTip("备注已保存 ✓", error: false);
	}

	// 发消息 → 直接进私聊(标题用备注优先的显示名;此前左滑关闭的会话先恢复)
	async void OnMsgClicked(object? sender, EventArgs e)
	{
		var key = $"{Protocol.ConvKeyPrivate}{_userId}";
		Shell.Current.Navigation.NavigationStack.OfType<MainPage>().LastOrDefault()?.ReopenConv(key);
		await Shell.Current.GoToAsync(
			$"chat?title={Uri.EscapeDataString(DisplayName())}&key={key}&isGroup=false");
	}

	void OnDeleteClicked(object? sender, EventArgs e)
	{
		ConfirmUi.TitleText = "删除好友";
		ConfirmUi.MessageText = $"确定删除好友「{DisplayName()}」吗?删除后双方好友列表都会移除,聊天记录保留。";
		ConfirmUi.ConfirmText = "删除";
		ConfirmUi.IsDanger = true;
		OpenCard(ConfirmUi);
	}

	void OnConfirmAccepted(object? sender, EventArgs e)
	{
		CloseOverlays();
		if (!AppState.Client.IsConnected)
		{
			ShowTip("没连接服务器", error: true);
			return;
		}
		AppState.Client.SendDeleteFriend(_userId); // 服务器推 friendDeleted 给双方(列表随之移除)
		_ = Shell.Current.GoToAsync("..");
	}

	// ---- 状态提示(红 = 错误,绿 = 成功,桌面 FriendDetailDialog 的 ErrorText 位) ----

	void ShowTip(string text, bool error)
	{
		PageTip.Text = text;
		PageTip.TextColor = error ? Color.FromArgb("#E05B4C") : Color.FromArgb("#58B368");
		PageTip.IsVisible = true;
	}

	void HideTip() => PageTip.IsVisible = false;

	// ---- 弹层开关 ----

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

	void CloseOverlays(View? keepOpened = null)
	{
		if (!ReferenceEquals(PromptUi, keepOpened)) PromptUi.IsVisible = false;
		if (!ReferenceEquals(ConfirmUi, keepOpened)) ConfirmUi.IsVisible = false;
		if (keepOpened is null)
		{
			Overlay.IsVisible = false;
			KeyboardUtil.HideKeyboard(); // C3:备注输入卡关掉后输入法还常驻挡屏
		}
	}

	void OnOverlayDimTapped(object? sender, TappedEventArgs e) => CloseOverlays();
	void OnOverlayCanceled(object? sender, EventArgs e) => CloseOverlays();

	protected override bool OnBackButtonPressed()
	{
		if (Overlay.IsVisible) { CloseOverlays(); return true; }
		return base.OnBackButtonPressed();
	}
}
