using QPet.Core;
using QPet.Mobile.Models;

namespace QPet.Mobile.Views.Controls;

/// <summary>
/// 用户主页卡(桌面 UserPopup 同款):群聊气泡点头像查看某人。
/// Bind(profile) 后按身份切尾部:自己 → "这是你自己";已是好友 → 绿字"已经是好友";
/// 申请已发 → "申请已发送,等待对方同意"(无按钮);陌生人 → ＋ 添加好友(点后变已申请)。
/// </summary>
public partial class UserProfileCard : ContentView
{
	/// <summary>当前展示的用户 Id(宿主收到 AddRequested 后发好友申请)。</summary>
	public string TargetUserId { get; private set; } = "";

	public UserProfileCard()
	{
		InitializeComponent();
	}

	/// <summary>填充用户信息并切状态(每次弹窗前调用,状态重置)。</summary>
	public void Bind(UserProfile profile)
	{
		TargetUserId = profile.UserId;
		NickLabel.Text = profile.Nickname;
		AccountLabel.Text = $"账号: {profile.Account}";
		AvatarBg.BackgroundColor = AvatarPalette.Pick(profile.UserId);
		AvatarCharLabel.Text = profile.Nickname.Length > 0 ? profile.Nickname[..1] : "?";

		AddButton.IsVisible = false;
		AddButton.Text = "＋ 添加好友";
		AddButton.IsEnabled = true;

		if (profile.IsMe)
		{
			StatusLabel.Text = "这是你自己";
			StatusLabel.TextColor = Color.FromArgb("#C4702F");
		}
		else if (profile.IsFriend)
		{
			StatusLabel.Text = "已经是好友";
			StatusLabel.TextColor = Color.FromArgb("#58B368");
		}
		else if (profile.HasPendingRequest)
		{
			StatusLabel.Text = "申请已发送,等待对方同意";
			StatusLabel.TextColor = Color.FromArgb("#B4A28F");
		}
		else
		{
			StatusLabel.Text = "";
			AddButton.IsVisible = true;
		}
	}

	public event EventHandler? AddRequested;

	void OnAddClicked(object? sender, EventArgs e)
	{
		AddButton.Text = "已申请";
		AddButton.IsEnabled = false;
		AddRequested?.Invoke(this, EventArgs.Empty);
	}
}
