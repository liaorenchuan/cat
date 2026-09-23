using QPet.Core;

namespace QPet.Mobile.Models;

/// <summary>群聊页签列表行。Title = 云端群备注优先的显示名(公共群固定原名);
/// 副行 = 群号 + 人数;👥 头像由 XAML 静态绘制。</summary>
public class GroupRow
{
	public string GroupId { get; init; } = "";
	public string Num { get; init; } = "";
	public string Title { get; init; } = "";
	public string Sub { get; init; } = "";

	/// <summary>行内容签名(内容没变就不重绑列表,滚动位置不跳)。</summary>
	public string Signature => $"{GroupId}|{Num}|{Title}|{Sub}";
}

/// <summary>群列表构建规则(桌面 ChatView.RefreshGroupList 同构):
/// Title = remark 优先(Setting "GroupRemark.{groupId}",只自己可见;公共群固定原名);
/// 群号非空显 "群号 {Num} · 成员 {N} 人",无号只显人数。</summary>
public static class GroupFeed
{
	public static List<GroupRow> Build(IEnumerable<GroupInfo> groups, IReadOnlyDictionary<string, string> settings)
	{
		var rows = new List<GroupRow>();
		foreach (var g in groups)
		{
			var title = g.Name;
			if (g.GroupId != "public")
			{
				var remark = settings.GetValueOrDefault("GroupRemark." + g.GroupId) ?? "";
				if (remark.Length > 0)
					title = remark;
			}
			rows.Add(new GroupRow
			{
				GroupId = g.GroupId,
				Num = g.Num,
				Title = title,
				Sub = g.Num.Length > 0
					? $"群号 {g.Num} · 成员 {g.Members.Count} 人"
					: $"成员 {g.Members.Count} 人",
			});
		}
		return rows;
	}
}

/// <summary>搜索加群结果行(桌面 SearchGroupDialog.GroupItem 同构):类型徽(公共群橙/好友群灰)+
/// 状态按钮(你创建的群 / 已在群内 / 已申请 / ＋ 加入 / ✉ 申请)。
/// 点击后直接改 R 上的标志(IsMember/HasPendingRequest,服务器模型可变),重绑即新态。</summary>
public class GroupSearchRow
{
	public GroupSearchResult R { get; init; } = null!;
	public string Name => R.Name;
	public string Sub => $"群号 {R.Num} · {R.MemberCount} 人";
	public string KindText => R.IsPublic ? "公共群" : "好友群";
	public Color KindBg => R.IsPublic ? Color.FromArgb("#FFE9D2") : Color.FromArgb("#F0F0F0");
	public Color AvatarBg => Color.FromArgb("#E5F6E0");
	public string BtnText => R.IsOwner ? "你创建的群" : R.IsMember ? "已在群内"
		: R.HasPendingRequest ? "已申请" : R.IsPublic ? "＋ 加入" : "✉ 申请";
	public bool BtnEnabled => !R.IsOwner && !R.IsMember && !R.HasPendingRequest;
}

/// <summary>创建群页好友多选行:点行切换勾选,右侧圆圈 ✓ 由 Selected 派生。
/// 显示名备注优先(与好友列表/建群对话框同规则)。</summary>
public class GroupPickRow
{
	public FriendInfo Friend { get; init; } = null!;
	public bool Selected { get; set; }

	public string Nick => Friend.Remark.Length > 0 ? Friend.Remark : Friend.Nickname;
	public string Sub => $"账号 {Friend.Account}";
	public string AvatarText => Nick.Length > 0 ? Nick[..1] : "?";
	public Color AvatarBg => AvatarPalette.Pick(Friend.UserId);

	public Color CheckBg => Selected ? Color.FromArgb("#FF8C42") : Colors.White;
	public Color CheckStroke => Selected ? Color.FromArgb("#FF8C42") : Color.FromArgb("#F0E0CC");
	public string CheckMark => Selected ? "✓" : "";
}

/// <summary>
/// 群成员行(桌面 MembersDialog.MemberItem 同构)。角色徽:我 &gt; 群主 &gt; 管理 &gt; 好友
/// (群主优先于管理:AdminIds 可能含群主);群主/管理员也保留"＋ 好友"按钮,只有自己/已是好友才隐藏。
/// 行尾操作:＋ 好友(非我非好友);设为/取消管理(仅群主视角,对方非我非群主);
/// 🚫 移出(群主可移任何人除自己/群主,管理员可移普通成员)。
/// 发好友申请/设管理/移出后由本地状态翻转或整体重建刷新。
/// </summary>
public class GroupMemberRow
{
	public string UserId { get; init; } = "";
	public string Nickname { get; init; } = "";
	public string Account { get; init; } = "";
	public bool Online { get; init; }
	public bool IsMe { get; init; }
	public bool IsFriend { get; init; }
	/// <summary>查看者视角:群主(可设管理/移任何人)。</summary>
	public bool IsOwnerRole { get; init; }
	/// <summary>查看者视角:管理员(可移普通成员)。</summary>
	public bool IsAdminRole { get; init; }
	public string OwnerId { get; init; } = "";
	/// <summary>该成员是管理员(群主设/取消后本地翻转,groupUpdated 整体重建)。</summary>
	public bool IsAdmin { get; set; }
	public bool AddSent { get; set; }       // 本页已发好友申请(服务器去重,本地先行置灰)

	public string Nick => Nickname.Length > 0 ? Nickname : "?";
	public string Sub => Account.Length > 0 ? $"账号 {Account}" : "";
	public bool HasSub => Account.Length > 0;
	public string AvatarChar => Nick[..1];
	public Color AvatarBg => AvatarPalette.Pick(UserId);
	public Color OnlineBrush => Online ? Color.FromArgb("#58B368") : Color.FromArgb("#C9C9C9");

	/// <summary>角色徽文本:我 &gt; 群主 &gt; 管理 &gt; 好友(与桌面 MemberItem.ActionText 同序)。</summary>
	public string RoleTag => IsMe ? "我"
		: UserId == OwnerId ? "群主"
		: IsAdmin ? "管理"
		: IsFriend ? "好友" : "";
	public Color RoleBg => IsMe ? Color.FromArgb("#FFE9D2")
		: UserId == OwnerId ? Color.FromArgb("#FFE4C8")
		: IsAdmin ? Color.FromArgb("#FFF0DC")
		: IsFriend ? Color.FromArgb("#F0F0F0")
		: Colors.Transparent;
	public bool HasRole => RoleTag.Length > 0;

	// ---- 行尾操作(桌面 MemberItem 按钮可见性同构) ----
	public bool ShowAdd => !IsMe && !IsFriend;
	public string AddText => AddSent ? "已发送" : "＋ 好友";
	public bool AddEnabled => !AddSent;
	public bool ShowAdminBtn => IsOwnerRole && !IsMe && UserId != OwnerId;
	public string AdminBtnText => IsAdmin ? "取消管理" : "设为管理";
	public bool ShowKick => !IsMe && UserId != OwnerId && (IsOwnerRole || (IsAdminRole && !IsAdmin));

	public void MarkAddSent() => AddSent = true;
}

/// <summary>成员页底部"可拉好友"行(桌面 MembersDialog.FriendOption 同构):备注优先的标题。</summary>
public class InviteOptionRow
{
	public string UserId { get; init; } = "";
	public string Title { get; init; } = "";
	public bool Sent { get; set; } // 已发邀请:按钮禁用(等对方同意,同桌面防重复)

	public string AvatarChar => Title.Length > 0 ? Title[..1] : "?";
	public Color AvatarBg => AvatarPalette.Pick(UserId);
	public bool BtnEnabled => !Sent;
}
