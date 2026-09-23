using QPet.Core;

namespace QPet.Mobile.Models;

/// <summary>
/// 头像底色盘:按昵称/账号轮转取浅色(与桌面端各好友颜色块观感一致)。
/// 自算哈希:跨进程稳定,换账号重登颜色不跳。
/// </summary>
public static class AvatarPalette
{
	private static readonly Color[] Palette =
	[
		Color.FromArgb("#FFE4C8"), Color.FromArgb("#FFD9AC"), Color.FromArgb("#FFF0DC"),
		Color.FromArgb("#E5F6E0"), Color.FromArgb("#FFC49E"), Color.FromArgb("#F0E0CC"),
	];

	public static Color Pick(string seed)
	{
		var hash = 0;
		foreach (var c in seed)
			hash = hash * 31 + c;
		return Palette[(hash & 0x7FFFFFFF) % Palette.Length];
	}
}

/// <summary>会话行(消息页列表,协议数据驱动)。字段名即 ConvPanel XAML 绑定名。</summary>
public class ConvRow
{
	/// <summary>协议会话键 "0:对方UserId" / "1:群Id"(聊天页路由与未读字典共用)。</summary>
	public string Key { get; init; } = "";
	public bool IsGroup { get; init; }
	/// <summary>列表显示名(好友=备注优先;群=群名,构建时已算好)。</summary>
	public string Title { get; init; } = "";
	public string Preview { get; init; } = "";
	public string TimeText { get; init; } = "";
	public int Unread { get; init; }
	public bool Online { get; init; }
	/// <summary>排序用:最后消息本地时钟刻度(无消息 = 0 排最后)。</summary>
	public long LastTicks { get; init; }
	public string AvatarText { get; init; } = "";
	public required Color AvatarBg { get; init; }

	public string UnreadText => Unread > 99 ? "99+" : Unread.ToString();

	/// <summary>行内容签名:列表重绑前比较,内容没变就跳过(避免滚动位置被重置)。
	/// 只要覆盖了界面上会显示的字段即可,顺序无关。</summary>
	public string Signature => $"{Key}|{Title}|{Preview}|{TimeText}|{Unread}|{Online}|{LastTicks}";
	public bool HasUnread => Unread > 0;
	public bool ShowOnlineDot => !IsGroup;
	public Color OnlineBrush => Online ? Color.FromArgb("#58B368") : Color.FromArgb("#C9C9C9");
}

/// <summary>
/// 会话列表构建规则(对齐桌面 ChatView.RefreshConvList):
/// 好友 + 群各占一行,按最后消息时间倒序(无消息排最后;群在前,公共群固定最前);
/// 群标题 = 云端群备注优先的显示名(桌面 GroupDisplayName,备注只自己可见,公共群固定原名);
/// 已删除会话本地隐藏(新消息到达自动恢复,由调用方从 hidden 集合移除)。
/// </summary>
public static class ConvFeed
{
	public static List<ConvRow> Build(
		List<GroupInfo> groups, List<FriendInfo> friends,
		IReadOnlyDictionary<string, int> unread, IReadOnlyDictionary<string, ChatMessage> lastPreview,
		IReadOnlySet<string> hiddenConvs, bool connected, string me,
		IReadOnlyDictionary<string, string> settings)
	{
		var rows = new List<ConvRow>();

		// 群:公共群固定最前,无消息显示成员数
		foreach (var g in groups.OrderBy(g => g.GroupId == "public" ? 0 : 1))
		{
			var key = $"{Protocol.ConvKeyGroup}{g.GroupId}";
			if (hiddenConvs.Contains(key)) continue; // 已删除的会话:列表不显示(新消息来才恢复)
			var last = lastPreview.GetValueOrDefault(key);
			var title = g.Name;
			if (g.GroupId != "public")
			{
				var remark = settings.GetValueOrDefault("GroupRemark." + g.GroupId) ?? "";
				if (remark.Length > 0)
					title = remark;
			}
			rows.Add(new ConvRow
			{
				Key = key, IsGroup = true, Title = title,
				Preview = last is null ? $"成员 {g.Members.Count} 人" : PreviewText(last, me),
				TimeText = last is null ? "" : ConvTimeText(last),
				Unread = unread.GetValueOrDefault(key),
				LastTicks = last?.Time.ToLocalTime().Ticks ?? 0,
				AvatarText = "👥", AvatarBg = Color.FromArgb("#E5F6E0"),
			});
		}

		// 好友:备注优先,无消息显示"暂无消息";在线点受连接状态约束(断网全灰)
		foreach (var f in friends)
		{
			var key = $"{Protocol.ConvKeyPrivate}{f.UserId}";
			if (hiddenConvs.Contains(key)) continue;
			var last = lastPreview.GetValueOrDefault(key);
			var name = f.Remark.Length > 0 ? f.Remark : f.Nickname;
			rows.Add(new ConvRow
			{
				Key = key, Title = name,
				Preview = last is null ? "暂无消息" : PreviewText(last, me),
				TimeText = last is null ? "" : ConvTimeText(last),
				Unread = unread.GetValueOrDefault(key),
				LastTicks = last?.Time.ToLocalTime().Ticks ?? 0,
				Online = f.Online && connected, // 断网/离线:全部置灰
				AvatarText = name.Length > 0 ? name[..1] : "?",
				AvatarBg = AvatarPalette.Pick(f.UserId),
			});
		}

		return rows.OrderByDescending(r => r.LastTicks).ToList();
	}

	/// <summary>列表预览文案:群聊别人发的带昵称前缀,自己发的带"我:",系统消息原文。
	/// 只取第一行,超长截到 10 字符(桌面 PreviewText 同款)。</summary>
	public static string PreviewText(ChatMessage m, string me)
	{
		string prefix;
		if (m.Type == 1)
			prefix = ""; // 系统消息文本自带语境("「X」加入了群聊")
		else if (m.ConvType == 1 && m.FromUserId != me && m.FromName.Length > 0)
			prefix = m.FromName + ": ";
		else if (m.FromUserId == me)
			prefix = "我: ";
		else
			prefix = "";
		var text = m.Text;
		var nl = text.IndexOf('\n');
		if (nl >= 0)
			text = text[..nl].TrimEnd(); // 多行消息只留第一行
		if (text.Length > 10)
			text = text[..10]; // 只显示前 10 个字符
		return prefix + text;
	}

	/// <summary>会话列表时间:今天 HH:mm / 昨天 / MM-dd。</summary>
	public static string ConvTimeText(ChatMessage m)
	{
		var t = m.Time.ToLocalTime();
		var today = DateTime.Today;
		if (t.Date == today)
			return t.ToString("HH:mm");
		if (t.Date == today.AddDays(-1))
			return "昨天";
		return t.ToString("MM-dd");
	}
}
