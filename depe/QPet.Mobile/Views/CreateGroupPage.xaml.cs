using QPet.Core;
using QPet.Mobile.Models;
using QPet.Mobile.Services;
using QPet.Mobile.Utils;

namespace QPet.Mobile.Views;

/// <summary>
/// 创建群页:群名 + 好友多选(点行勾选,计数实时)。CreateGroup 真实建群(桌面 CreateGroupDialog
/// 同款文案/逻辑):服务器建群并向所选好友发出邀请,对方同意后进群(邀请制,进群与群内成员列表
/// 由 groupUpdated 推送驱动);创建成功绿字提示后 ~1.2s 自动关页。
/// 好友列表来自主界面共享快照(welcome 全量 + FriendAdded/FriendDeleted 增量,与好友页一致)。
/// </summary>
public partial class CreateGroupPage : ContentPage
{
	private readonly List<GroupPickRow> _rows = [];
	private bool _closed; // 已关页(建群成功的 1.2 秒里用户自己返回时,别再弹一层)

	public CreateGroupPage()
	{
		InitializeComponent();
		FriendList.ItemsSource = _rows;
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		// 每次出现重建:备注/好友增删实时反映(桌面每次打开对话框重新取好友)
		_rows.Clear();
		foreach (var f in AppState.Client.LastSnapshot.Friends)
			_rows.Add(new GroupPickRow { Friend = f });
		Render();
	}

	async void OnCloseClicked(object? sender, EventArgs e)
	{
		_closed = true;
		await Shell.Current.GoToAsync("..");
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		// 用户已自己返回(含返回键/手势):建群成功的延时关页要放弃,否则会把主界面也弹掉
		_closed = true;
		KeyboardUtil.HideKeyboard();
	}

	void OnNameChanged(object? sender, TextChangedEventArgs e) => RefreshCanCreate();

	// 点行切换勾选并重建列表(行无 INPC)
	void OnFriendTapped(object? sender, TappedEventArgs e)
	{
		if (e.Parameter is not GroupPickRow row)
			return;
		row.Selected = !row.Selected;
		Render();
	}

	void Render()
	{
		FriendList.ItemsSource = null;
		FriendList.ItemsSource = _rows;
		CountText.Text = $"选择好友 {_rows.Count(r => r.Selected)}/{_rows.Count}";
		RefreshCanCreate();
	}

	void RefreshCanCreate()
	{
		var named = NameInput.Text?.Trim().Length > 0;
		var picked = _rows.Any(r => r.Selected);
		CreateBtn.IsEnabled = named && picked;
	}

	async void OnCreateClicked(object? sender, EventArgs e)
	{
		var name = NameInput.Text?.Trim() ?? "";
		var selected = _rows.Where(r => r.Selected).Select(r => r.Friend).ToList();
		// 按钮未亮时点不到,这里兜底(桌面 OnCreate 校验与文案同款)
		if (name.Length == 0) { ShowTip("请输入群名称", error: true); return; }
		if (name.Length > 20) { ShowTip("群名最多 20 字", error: true); return; }
		if (selected.Count == 0) { ShowTip("至少选择一个好友", error: true); return; }
		if (!AppState.Client.IsConnected)
		{
			ShowTip("没连接服务器", error: true);
			return;
		}
		AppState.Client.CreateGroup(name, selected.Select(f => f.UserId).ToList());
		ShowTip("已创建群聊,已向所选好友发出邀请,同意后进群", error: false);
		CreateBtn.IsEnabled = false; // 防重复提交(同桌面创建后禁用)
		await Task.Delay(1200);      // 桌面 DispatcherTimer 同款自动关页
		if (_closed)
			return; // 这 1.2 秒里用户已经自己返回了:再弹一次会连带把主界面弹掉(闪屏重载)
		_closed = true;
		await Shell.Current.GoToAsync("..");
	}

	void ShowTip(string text, bool error)
	{
		PageTip.Text = text;
		PageTip.TextColor = error ? Color.FromArgb("#E05B4C") : Color.FromArgb("#58B368");
		PageTip.IsVisible = true;
	}
}
