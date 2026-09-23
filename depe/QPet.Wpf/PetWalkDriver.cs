using System.Diagnostics;
using System.Windows.Media;
using QPet.Core;

namespace QPet.Wpf;

/// <summary>
/// 走动驱动:PetWalker 引擎 + Stopwatch 时钟 + 渲染帧订阅,悬浮窗 / 宠物小屋共用。
/// 引擎只算 X 轴往返;每帧推进后回调宿主摆画面(窗口 Left / 宠物 X + 垂直跟随)。
/// 边界(Min/Max/MinY/MaxY)由宿主按各自布局设置,速度等硬编码值保持引擎默认。
/// </summary>
internal sealed class PetWalkDriver
{
    private readonly Stopwatch _clock = new();
    private TimeSpan _lastTick;
    private bool _walking; // 走动驱动是否已挂到渲染帧(CompositionTarget.Rendering)

    public PetWalkDriver(PetWalker walker)
    {
        Walker = walker;
        // 掉头转发给宿主(切换走路帧 / 随机垂直目标)
        walker.DirectionChanged += f => FacingChanged?.Invoke(f);
    }

    /// <summary>走动引擎(宿主设置 Min/Max/MinY/MaxY 边界,读取 Position/FacingRight)。</summary>
    public PetWalker Walker { get; }

    /// <summary>掉头时触发,参数 true=向右(转发引擎 DirectionChanged)。</summary>
    public event Action<bool>? FacingChanged;

    /// <summary>每渲染帧回调,参数 = 本帧时长(秒);引擎已推进,宿主负责摆画面。</summary>
    public event Action<double>? PositionChanged;

    /// <summary>开始走动并挂渲染帧驱动;fromCenter:从范围中点启动(范围无效时不启动)。</summary>
    public bool StartWalking(bool fromCenter = false)
    {
        if (fromCenter && !Walker.StartAtCenter())
            return false;

        _clock.Restart();
        _lastTick = TimeSpan.Zero;
        // 渲染帧驱动(与屏幕刷新同步,60fps+,比定时器 30ms 丝滑):仅走动时挂载
        if (!_walking)
        {
            _walking = true;
            CompositionTarget.Rendering += OnRenderFrame;
        }
        return true;
    }

    /// <summary>停止走动:卸载渲染帧驱动。</summary>
    public void StopWalking()
    {
        _walking = false;
        CompositionTarget.Rendering -= OnRenderFrame;
    }

    private void OnRenderFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var delta = now - _lastTick;
        _lastTick = now;
        Walker.Update(delta);
        PositionChanged?.Invoke(delta.TotalSeconds);
    }
}
