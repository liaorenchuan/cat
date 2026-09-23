using System.Windows.Media;
using QPet.Ui;

namespace QPet.Admin;

/// <summary>带昵称/在线状态的条目模型基类(在线文字/在线圆点/首字头像;GridView 绑定只认属性)。</summary>
public abstract class RowViewBase
{
    public string Nickname { get; set; } = "";
    public bool Online { get; set; }

    public string OnlineText => Online ? "● 在线" : "离线";
    public Brush OnlineBrush => Online ? UiPalette.Green : Brushes.Silver;

    // 头像:首字 + 浅橙圆底(与账号详情页头像同款)
    public string AvatarChar => Nickname.Length > 0 ? Nickname.Substring(0, 1) : "?";
    public Brush AvatarBrush => UiPalette.SurfacePressed;
}

/// <summary>群条目模型基类:群号/群名/类型/人数 + 类型徽章色(各页展示文案不同,留在子类)。</summary>
public abstract class GroupRowViewBase
{
    public string GroupId { get; set; } = "";
    public string Num { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsPublic { get; set; }
    public int MemberCount { get; set; }

    public Brush KindBrush => IsPublic ? UiPalette.Warm : UiPalette.Muted;
}
