using QPet.Core;
using QPet.Mobile.Models;
using QPet.Mobile.Services;
using QPet.Mobile.Utils;
using QPet.Mobile.Views.Controls;

namespace QPet.Mobile.Views;

/// <summary>
/// 群详情页:groupdetail?groupId=…(桌面 GroupDetailDialog 的移动端对应)。
/// 头部 = 群原始信息(群名/群号/人数/创建者昵称);改名全员可见(空 = 恢复原群名文案,
/// >20 拦截),群备注 = 云端 Setting("GroupRemark.{groupId}",只自己可见,换设备登录也有,
/// 空 = 取消;服务器回推 settingUpdated 自动同步);公共群固定原名:无改名/备注/退出(桌面同规则);
/// 退出群聊确认文案与桌面逐字一致,群主退出 = 解散,由 groupRemoved 兜底关页。
/// 数据 = 主界面共享快照(welcome 全量 + groupUpdated 增量,各处改动即时反映)。
/// </summary>
public partial class GroupDetailPage : ContentPage, IQueryAttributable
{
	private string _groupId = "";
	private string _promptAction = "";   // rename / remark
	private bool _live;
	private bool _leaving;               // 群被解散自动关页中:防重复导航

	public GroupDetailPage()
	{
		InitializeComponent();
	}

	public void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		_groupId = query.TryGetValue("groupId", out var v) ? v.ToString() ?? "" : "";
		_promptAction = "";
		CloseOverlays();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		if (!_live)
		{
			_live = true;
			AppState.Client.GroupRemoved += OnGroupRemoved; // 群被解散/我被移出:详情页随之关闭
		}
		Reload();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (NavGuard.IsCovered(this))
			return; // 被聊天页/成员页覆盖:保持订阅(回来 Reload 取最新镜像)
		if (!_live)
			return;
		_live = false;
		AppState.Client.GroupRemoved -= OnGroupRemoved;
	}

	void OnGroupRemoved(string groupId, string reason)
	{
		if (_live && !_leaving && groupId == _groupId)
		{
			_leaving = true;
			_ = Shell.Current.GoToAsync("..");
		}
	}

	/// <summary>云端群备注(welcome 快照 / settingUpdated 回推缓存,空 = 无备注;只自己可见)。</summary>
	string RemarkOf(GroupInfo g) =>
		AppState.Client.LastSnapshot.Settings.GetValueOrDefault("GroupRemark." + g.GroupId) ?? "";

	// 每次出现重读镜像(群名/成员/备注可能刚被改过)
	void Reload()
	{
		HideTip();
		var group = AppState.Client.LastSnapshot.Groups.FirstOrDefault(x => x.GroupId == _groupId);
		if (group is null)
		{
			// 群已解散/被移出(事件通常已先关页,这里兜底)
			if (!_leaving)
			{
				_leaving = true;
				_ = Shell.Current.GoToAsync("..");
			}
			return;
		}
		var isPublic = group.IsPublic;
		var ownerNick = group.Members.FirstOrDefault(m => m.UserId == group.OwnerId)?.Nickname ?? "";

		NameText.Text = group.Name;
		SubText.Text = group.Num.Length > 0
			? $"群号 {group.Num} · 成员 {group.Members.Count} 人"
			: $"成员 {group.Members.Count} 人";
		RowNameText.Text = group.Name;
		var remark = RemarkOf(group);
		RemarkText.Text = remark.Length > 0 ? remark : "未设置";
		RemarkText.TextColor = remark.Length > 0
			? Color.FromArgb("#C4702F") : Color.FromArgb("#B4A28F");
		NumText.Text = group.Num;
		MemberText.Text = $"{group.Members.Count} 人"; // 人数行(点击整行进成员页)
		// 公共群全员共享固定原名:隐藏 改名/备注/创建者/退出(备注只自己可见,公共群无处承载,
		// 与改名同理整体隐藏;各分隔线随行走,避免残留孤线)
		RenameRow.IsVisible = !isPublic;
		RenameDivider.IsVisible = !isPublic;
		RemarkRow.IsVisible = !isPublic;
		RemarkDivider.IsVisible = !isPublic;
		OwnerRow.IsVisible = !isPublic && ownerNick.Length > 0;
		OwnerDivider.IsVisible = OwnerRow.IsVisible;
		OwnerText.Text = ownerNick;
		QuitGroupButton.IsVisible = !isPublic;
	}

	async void OnCloseClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");

	// 群名行 → 改名输入卡(桌面 GroupDetailDialog OnSaveName 同款文案/校验)
	void OnRenameRowTapped(object? sender, TappedEventArgs e)
	{
		var group = AppState.Client.LastSnapshot.Groups.FirstOrDefault(x => x.GroupId == _groupId);
		if (group is null || group.IsPublic)
			return; // 公共群不改名
		_promptAction = "rename";
		PromptUi.TitleText = "群聊改名";
		PromptUi.HintText = "输入新的群名称(最多 20 字)";
		PromptUi.PromptPlaceholder = "给群聊起个新名字";
		PromptUi.InitialValue = group.Name;
		OpenCard(PromptUi);
	}

	// 群备注行 → 输入卡(云端备注:空 = 取消;仅 >20 拦截;换设备也有)
	void OnRemarkRowTapped(object? sender, TappedEventArgs e)
	{
		_promptAction = "remark";
		PromptUi.TitleText = "群备注";
		PromptUi.HintText = "给群起个备注(空 = 取消备注,最多 20 字)";
		PromptUi.PromptPlaceholder = "给群聊加个备注";
		PromptUi.InitialValue = RemarkOf(AppState.Client.LastSnapshot.Groups
			.FirstOrDefault(x => x.GroupId == _groupId) ?? new GroupInfo { GroupId = _groupId });
		OpenCard(PromptUi);
	}

	void OnPromptAccepted(object? sender, EventArgs e)
	{
		var value = PromptUi.Value;
		var group = AppState.Client.LastSnapshot.Groups.FirstOrDefault(x => x.GroupId == _groupId);
		if (_promptAction == "rename")
		{
			if (value.Length == 0)
			{
				// 输入为空:不改名,输入框恢复原来的名字(桌面同文案)
				PromptUi.InitialValue = group?.Name ?? "";
				PromptUi.ShowError("群名不能为空,已恢复原群名");
				return;
			}
			if (value.Length > 20) { PromptUi.ShowError("群名最多 20 字"); return; }
			if (group is not null)
				group.Name = value; // 快照先行(groupUpdated 回推后替换,幂等)
			AppState.Client.RenameGroup(_groupId, value);
			CloseOverlays();
			Reload();
			ShowTip("已提交,群成员刷新后可见新群名 ✓", error: false);
		}
		else if (_promptAction == "remark")
		{
			if (value.Length > 20) { PromptUi.ShowError("备注最多 20 字"); return; }
			// 云端保存:服务器落库并回推本账号全部连接(消息页/其他设备同步生效)
			AppState.Client.SetSetting("GroupRemark." + _groupId, value); // 空 = 取消备注
			AppState.Client.LastSnapshot.Settings["GroupRemark." + _groupId] = value;
			CloseOverlays();
			Reload();
			ShowTip("备注已保存 ✓", error: false);
		}
	}

	// 成员数行 → 群成员页(groupId 路由参数;群头"👥 成员"同页)
	async void OnMembersRowTapped(object? sender, TappedEventArgs e) =>
		await Shell.Current.GoToAsync($"members?groupId={_groupId}");

	// 发消息 → 群聊会话(标题 = 备注优先的显示名;此前左滑关闭的会话先恢复)
	async void OnMsgClicked(object? sender, EventArgs e)
	{
		var key = $"{Protocol.ConvKeyGroup}{_groupId}";
		Shell.Current.Navigation.NavigationStack.OfType<MainPage>().LastOrDefault()?.ReopenConv(key);
		var title = GroupFeed.Build(
			AppState.Client.LastSnapshot.Groups.Where(g => g.GroupId == _groupId),
			AppState.Client.LastSnapshot.Settings).FirstOrDefault()?.Title ?? "群聊";
		await Shell.Current.GoToAsync(
			$"chat?title={Uri.EscapeDataString(title)}&key={key}&isGroup=true");
	}

	void OnQuitClicked(object? sender, EventArgs e)
	{
		var group = AppState.Client.LastSnapshot.Groups.FirstOrDefault(x => x.GroupId == _groupId);
		if (group is null || group.IsPublic)
			return;
		ConfirmUi.TitleText = "退出群聊";
		ConfirmUi.MessageText = $"确定退出群聊「{group.Name}」吗?\n(群主退出将解散该群,群聊记录一并删除)";
		ConfirmUi.ConfirmText = "退出";
		ConfirmUi.IsDanger = group.OwnerId == AppState.Client.MyUser.UserId;
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
		AppState.Client.LeaveGroup(_groupId); // 群主 = 解散(全员收 groupRemoved);成员 = 退出
		_ = Shell.Current.GoToAsync("..");
	}

	// ---- 状态提示(红 = 错误,绿 = 成功,桌面 GroupDetailDialog 的 ErrorText 位) ----

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
			KeyboardUtil.HideKeyboard(); // C3:群改名/群备注输入卡关掉后输入法还常驻挡屏
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
