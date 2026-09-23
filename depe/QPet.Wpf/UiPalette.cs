using System.Windows.Media;

namespace QPet.Ui;

/// <summary>
/// 暖橙主题色板(code-behind 侧;XAML 侧见 Themes/WarmOrange.xaml,保留的色值两处一致)。
/// Brush 均为只读冻结实例,可直接赋给控件属性。
/// </summary>
public static class UiPalette
{
    private static SolidColorBrush F(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>主橙(主要按钮)。</summary>
    public static readonly Brush Warm = F(0xFF, 0x8C, 0x42);

    /// <summary>更浅橙底(次级按钮按下)。</summary>
    public static readonly Brush SurfacePressed = F(0xFF, 0xE4, 0xC8);

    /// <summary>灰绿(离线状态点)。</summary>
    public static readonly Brush Muted = F(0x9A, 0xB0, 0x9A);

    // ---- 视图语义色(高频重复色收纳;与 WarmOrange.xaml 的 Ui* 资源同值) ----

    /// <summary>次级棕(离线状态点/徽章)。</summary>
    public static readonly Brush Brown = F(0xB0, 0x8A, 0x62);

    /// <summary>绿色浅底(在线标签/自己消息气泡)。</summary>
    public static readonly Brush GreenBg = F(0xE5, 0xF6, 0xE0);

    /// <summary>在线绿(在线状态点/注册模式主色)。</summary>
    public static readonly Brush Green = F(0x58, 0xB3, 0x68);

    /// <summary>我的消息气泡(浅杏橙)。</summary>
    public static readonly Brush MyBubble = F(0xFF, 0xC4, 0x9E);

    /// <summary>气泡深色文字(浅色气泡上)。</summary>
    public static readonly Brush BubbleText = F(0x4A, 0x3A, 0x28);

    // ---- 颜色值版(渐变 LinearGradientBrush 需要 Color 而非 Brush) ----

    /// <summary>应用窗口渐变顶(上段)。</summary>
    public static Color AppBgTopColor => Color.FromRgb(0xFF, 0xFD, 0xF9);

    /// <summary>应用窗口渐变底(下段)。</summary>
    public static Color AppBgColor => Color.FromRgb(0xFF, 0xF3, 0xE0);

    /// <summary>渐变顶橙(主按钮渐变上端)。</summary>
    public static Color WarmTopColor => Color.FromRgb(0xFF, 0xB0, 0x66);

    /// <summary>主橙(主要按钮)。</summary>
    public static Color WarmColor => Color.FromRgb(0xFF, 0x8C, 0x42);
}
