using System.Windows.Media;

namespace QPet.Ui;

/// <summary>
/// 暖橙主题色板(Admin code-behind 高频用色子集,全部成员均有引用):
/// XAML 用色走 Themes/WarmOrange.xaml 资源;code-behind 缺色在此补一个成员,
/// 不必与 XAML 全部对齐(Admin 只用到这 8 个)。
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

    /// <summary>主橙(公共群标记)。</summary>
    public static readonly Brush Warm = F(0xFF, 0x8C, 0x42);

    /// <summary>次级文字棕(次要按钮前景)。</summary>
    public static readonly Brush Secondary = F(0xB0, 0x7A, 0x45);

    /// <summary>更浅橙底(头像圆底)。</summary>
    public static readonly Brush SurfacePressed = F(0xFF, 0xE4, 0xC8);

    /// <summary>成功绿(操作成功提示)。</summary>
    public static readonly Brush Success = F(0x6A, 0xB0, 0x6A);

    /// <summary>灰绿(好友群标记)。</summary>
    public static readonly Brush Muted = F(0x9A, 0xB0, 0x9A);

    /// <summary>错误红(错误提示/禁用状态文字)。</summary>
    public static readonly Brush Error = F(0xE0, 0x5B, 0x4C);

    /// <summary>次级棕(在线状态次级色)。</summary>
    public static readonly Brush Brown = F(0xB0, 0x8A, 0x62);

    /// <summary>在线绿(在线状态点)。</summary>
    public static readonly Brush Green = F(0x58, 0xB3, 0x68);
}
