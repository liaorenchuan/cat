using System.Runtime.InteropServices;
using System.Windows;

namespace QPet.Wpf;

/// <summary>
/// 拖拽状态机:按下基准 + 阈值确认 + 增量定位,悬浮窗 / 宠物小屋共用。
/// 宿主按下时记录屏幕坐标与位置基准,移动时取增量应用到窗口或宠物;
/// 位移超过阈值(10px)才确认拖拽并触发 <see cref="DragConfirmed"/>(宿主切拖拽帧)。
/// 鼠标坐标走 Win32 光标屏幕坐标:不经过 WPF 坐标换算,窗口移动不会污染位移计算。
/// </summary>
internal sealed class PetDragTracker
{
    private const double Threshold = 10; // 位移超过阈值才算拖拽(点击手抖 / 双击不算)

    private Point _screenPress;   // 按下时的鼠标屏幕坐标(拖拽位移基准)

    /// <summary>确认拖拽时触发(宿主:切拖拽帧,onEnter 暂停走动与眨眼)。</summary>
    public event Action? DragConfirmed;

    /// <summary>本次按下已构成拖拽(否则是点击)。</summary>
    public bool Dragging { get; private set; }

    /// <summary>按下时的宿主位置基准(窗口 Left/Top 或宠物 X/Y,DIP)。</summary>
    public double PosX { get; private set; }

    /// <summary>按下时的宿主位置基准(窗口 Left/Top 或宠物 X/Y,DIP)。</summary>
    public double PosY { get; private set; }

    /// <summary>按下时的鼠标屏幕坐标(缩放模式等直接读取用)。</summary>
    public Point ScreenPress => _screenPress;

    /// <summary>按下:记录屏幕坐标与宿主位置基准,重置拖拽状态。</summary>
    public void Press(Point screen, double posX, double posY)
    {
        _screenPress = screen;
        PosX = posX;
        PosY = posY;
        Dragging = false;
    }

    /// <summary>移动:返回像素位移增量(确认后);未构成拖拽返回 null(位置不动)。</summary>
    public (double X, double Y)? Move(Point screen)
    {
        var dx = screen.X - _screenPress.X;
        var dy = screen.Y - _screenPress.Y;
        if (!Dragging)
        {
            // 未确认拖拽:位移小(点击手抖 / 双击)不动
            if (dx * dx + dy * dy <= Threshold * Threshold)
                return null;
            // 超过阈值才确认拖拽:基准重置为当前,避免宿主跳变
            Dragging = true;
            _screenPress = screen;
            DragConfirmed?.Invoke();
            return null;
        }
        return (dx, dy);
    }

    /// <summary>Win32 光标屏幕坐标(像素;与窗口 Left/Top 的换算由宿主按 DPI 做)。</summary>
    public static Point CursorScreenPosition()
    {
        GetCursorPos(out var pt);
        return new Point(pt.X, pt.Y);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
