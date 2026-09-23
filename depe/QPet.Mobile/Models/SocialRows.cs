using QPet.Core;

namespace QPet.Mobile.Models;

/// <summary>
/// 社交链行模型(好友列表 / 申请中心三页签 / 搜索好友结果),协议数据驱动。
/// 字段名与对应页面 XAML 模板绑定一致;申请时间格式对齐桌面 RequestsDialog(MM-dd HH:mm)。
/// </summary>

/// <summary>好友列表行(好友页签)。Title = 备注优先的显示名;副行有备注时露原昵称,常显账号;
/// 状态点/文字与消息页会话行同源(在线受连接约束,断网全灰)。</summary>
public class FriendRow
{
	public string UserId { get; init; } = "";
	public string Title { get; init; } = "";
	public string Sub { get; init; } = "";
	public string StatusWord { get; init; } = "离线";
	public Color StatusBrush { get; init; } = Color.FromArgb("#C9C9C9");
	public string AvatarChar { get; init; } = "?";
	public Color AvatarBg { get; init; } = Color.FromArgb("#FFE4C8");

	/// <summary>行内容签名(内容没变就不重绑列表,滚动位置不跳)。</summary>
	public string Signature => $"{UserId}|{Title}|{Sub}|{StatusWord}";
}

/// <summary>好友列表构建规则(与 ConvFeed 好友行同款:备注优先 + 头像同配色;空态由页面空视图兜底)。</summary>
public static class FriendFeed
{
	public static List<FriendRow> Build(IEnumerable<FriendInfo> friends, bool connected)
	{
		var rows = new List<FriendRow>();
		foreach (var f in friends)
		{
			var name = f.Remark.Length > 0 ? f.Remark : f.Nickname;
			if (name.Length == 0)
				name = f.Account;
			var on = connected && f.Online;
			rows.Add(new FriendRow
			{
				UserId = f.UserId,
				Title = name,
				Sub = f.Remark.Length > 0
					? $"原昵称 {f.Nickname} · 账号 {f.Account}"
					: $"账号 {f.Account}",
				StatusWord = on ? "在线" : "离线",
				StatusBrush = on ? Color.FromArgb("#58B368") : Color.FromArgb("#C9C9C9"),
				AvatarChar = name.Length > 0 ? name[..1] : "?",
				AvatarBg = AvatarPalette.Pick(f.UserId),
			});
		}
		return rows;
	}
}

/// <summary>好友申请行(申请中心页签0):留言 + 时间 + 头像(与好友列表同款 UserId 配色)。</summary>
public class FriendReqRow
{
	public FriendRequest Req { get; init; } = null!;
	public string FromNick => Req.FromName;
	public string TimeText => ReqRows.TimeText(Req.CreatedAtMs);
	public string Message => Req.Text;
	public string AvatarChar => Req.FromName.Length > 0 ? Req.FromName[..1] : "?";
	public Color AvatarBg => AvatarPalette.Pick(Req.FromUserId);
}

/// <summary>加群申请行(页签1:我是群主/管理员待审批)。Sub 文案对齐桌面:昵称 申请加入「群名」。</summary>
public class GroupReqRow
{
	public GroupRequest Req { get; init; } = null!;
	public string FromNick => Req.FromName;
	public string TimeText => ReqRows.TimeText(Req.CreatedAtMs);
	public string Sub => $"{Req.FromName} 申请加入「{Req.GroupName}」";
	public string AvatarChar => "👥";
	public Color AvatarBg => Color.FromArgb("#E5F6E0"); // 与群聊条目同款绿块
}

/// <summary>群邀请行(页签2:我待同意的进群邀请)。Sub 文案对齐桌面:昵称 邀请你加入「群名」。</summary>
public class InviteReqRow
{
	public GroupInvite Inv { get; init; } = null!;
	public string FromNick => Inv.FromName;
	public string TimeText => ReqRows.TimeText(Inv.CreatedAtMs);
	public string Sub => $"{Inv.FromName} 邀请你加入「{Inv.GroupName}」";
	public string AvatarChar => Inv.FromName.Length > 0 ? Inv.FromName[..1] : "?";
	public Color AvatarBg => AvatarPalette.Pick(Inv.FromUserId);
}

/// <summary>申请/邀请时间戳(桌面 RequestsDialog 同款 MM-dd HH:mm)。</summary>
internal static class ReqRows
{
	public static string TimeText(long createdAtMs) =>
		DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs).ToLocalTime().ToString("MM-dd HH:mm");
}

/// <summary>搜索好友结果行(AddFriendPage)。按钮文案对齐桌面:已是好友 / 已申请 / ＋ 添加。
/// 申请发出后本地 MarkSent 置"已申请"(与服务器下轮搜索结果一致)。</summary>
public class UserSearchRow
{
	public string UserId { get; init; } = "";
	public string Nickname { get; init; } = "";
	public string Account { get; init; } = "";
	public bool IsFriend { get; init; }
	public bool Requested { get; init; } // 服务器状态:已发申请待处理
	public bool Sent { get; set; }       // 本会话内刚发过申请(本地先行置灰,等服务器状态下轮搜索自带)
	public bool Pending => Requested || Sent;

	public string Sub => $"账号: {Account}";
	public string BtnText => IsFriend ? "已是好友" : Pending ? "已申请" : "＋ 添加";
	public bool BtnEnabled => !IsFriend && !Pending;
	public string AvatarChar => Nickname.Length > 0 ? Nickname[..1] : "?";
	public Color AvatarBg => AvatarPalette.Pick(UserId);

	public void MarkSent() => Sent = true;
}
