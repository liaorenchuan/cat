using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QPet.Core;
using QPet.Ui;

namespace QPet.Wpf.Views;
/// <summary>ChatView 分部:输入区(回车发送/换行/emoji 表情面板)。</summary>
public partial class ChatView
{
    /// <summary>输入框高度随内容自适应:一行 45,每加一行 +24,封顶 90(输入条整体随之撑高)。</summary>
    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        var lines = MsgInput.LineCount;
        MsgInput.Height = Math.Min(45 + Math.Max(0, lines - 1) * 24, 90);
    }

    /// <summary>换行圆钮(😊 与「发送」之间):在光标处插入换行(等效 Shift+Enter,Enter 已用作发送)。</summary>
    private void OnNewLine(object sender, RoutedEventArgs e)
    {
        var idx = MsgInput.CaretIndex;
        MsgInput.Text = MsgInput.Text.Insert(idx, "\n");
        MsgInput.CaretIndex = idx + 1;
        MsgInput.Focus();
    }

    /// <summary>Enter 发送(多行模式),Shift+Enter 换行。
    /// 用 PreviewKeyDown:TextBox 类处理会先消费 Enter(插入换行),KeyDown 阶段拦截不到。</summary>
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            Send();
            e.Handled = true; // 阻止 TextBox 插入换行
        }
    }

    private void OnSend(object sender, RoutedEventArgs e) => Send();

    private void Send()
    {
        var text = MsgInput.Text.Trim();
        if (text.Length == 0)
            return;
        if (!_client.IsConnected) // 离线登录 / 断网退避期:不发,提示(不清输入,用户可复制/重发)
        {
            StatusText.Text = "没连接服务器";
            return;
        }
        // 消息经 ChatReceived 事件统一回来追加(本地回显),这里只发帧
        _client.SendChat(_convType, _convId, text);
        MsgInput.Clear();
        MsgInput.Focus(); // 点发送按钮后焦点回到输入框,可连续打字
    }

    // ---- emoji 快捷输入 ----

    private static readonly string[] Emojis =
    {
        "😀", "😄", "😁", "😂", "🤣", "😊", "😍", "🥰", "😘", "😎", "🤗", "🤔", "🙄", "😴", "🤤", "😭",
        "😅", "😉", "😜", "🤩", "🥳", "😇", "👍", "👏", "🙏", "💪", "🤝", "👌", "✌️", "❤️", "💕", "💖",
        "🎉", "🎂", "🍰", "☕", "🍺", "🎮", "🎯", "🏆", "🌹", "🌸", "🌈", "⭐", "🔥", "💯", "🐱", "🐶",
        "🐰", "🍀",
    };

    /// <summary>点 😊:展开/收起输入框下方的表情面板(内嵌占布局,消息区随之压缩/恢复)。</summary>
    private void OnEmojiToggle(object sender, RoutedEventArgs e)
    {
        EmojiPanel.Visibility = EmojiPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        if (EmojiPanel.Visibility == Visibility.Visible)
            MsgInput.Focus(); // 展开表情时输入框保持焦点,可直接打字
    }

    /// <summary>点 emoji:插入光标处并保持焦点(连续插入方便)。</summary>
    private void OnEmojiClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock tb)
            return;
        var idx = MsgInput.CaretIndex;
        MsgInput.Text = MsgInput.Text.Insert(idx, tb.Text);
        MsgInput.CaretIndex = idx + tb.Text.Length;
        MsgInput.Focus();
    }
}
