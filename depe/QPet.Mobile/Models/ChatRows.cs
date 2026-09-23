using System.ComponentModel;
using System.Runtime.CompilerServices;
using QPet.Core;

namespace QPet.Mobile.Models;

/// <summary>
/// 聊天消息行(气泡列表,协议数据驱动)。字段名与 ChatPage XAML 模板绑定一致,
/// 行分四类:日期栏(Msg=null)/系统消息(IsSystem)/对方消息(IsLeft)/我的消息(IsRight)。
/// 已读标 ReadText 可变(收到已读回执重算):带变更通知,行内刷新,不再整列重建列表。
/// </summary>
public class ChatMsgRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	/// <summary>消息(日期栏为空)。</summary>
	public ChatMessage? Msg { get; init; }

	/// <summary>日期栏文案(今天/昨天/yyyy年M月d日 星期x),消息行不用。</summary>
	public string DateText { get; init; } = "";

	public bool IsDateBar => Msg is null;
	public bool IsSystem => Msg is { Type: 1 };
	public bool IsMine { get; init; }                    // 我的消息(Type==1 系统消息由服务器发,恒非我)
	public bool IsGroup { get; init; }                   // 群聊:昵称显示用(私聊双方知底,省一栏)

	/// <summary>行主体文本:日期文案(日期栏)/系统提示/消息正文。</summary>
	public string Text => IsDateBar ? DateText : Msg?.Text ?? "";

	/// <summary>气泡时间 HH:mm(本地时区)。</summary>
	public string Time { get; init; } = "";

	/// <summary>群聊发送者昵称(我的消息 = 消息体里的昵称,历史固定)。</summary>
	public string Name { get; init; } = "";
	public bool ShowName => IsGroup && Name.Length > 0 && !IsDateBar && !IsSystem;

	public string AvatarChar { get; init; } = "";
	public Color AvatarBg { get; init; } = Color.FromArgb("#FFF0DC");

	/// <summary>头像右下状态点:仅私聊对方消息(绿 = 连接且对方在线,灰 = 离线/断网)。</summary>
	public bool AvatarDotVisible { get; init; }
	public Color AvatarDotBrush { get; init; } = Color.FromArgb("#C9C9C9");

	/// <summary>我的消息已读标:已读 / 已读 N/总 / 未读。变更通知触发依赖属性一并刷新(行内更新,不重建列表)。</summary>
	string _readText = "";
	public string ReadText
	{
		get => _readText;
		set
		{
			if (_readText == value)
				return;
			_readText = value;
			OnChanged();
			OnChanged(nameof(HasReadText));
			OnChanged(nameof(ReadDone));
			OnChanged(nameof(ReadBrush));
		}
	}
	public bool HasReadText => ReadText.Length > 0;
	public bool ReadDone => HasReadText && !ReadText.StartsWith("未读");
	public Color ReadBrush => ReadDone ? Color.FromArgb("#58B368") : Color.FromArgb("#C9B4A0");

	public bool IsLeft => !IsDateBar && !IsSystem && !IsMine;
	public bool IsRight => !IsDateBar && !IsSystem && IsMine;
}
