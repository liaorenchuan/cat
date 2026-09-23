namespace QPet.Core;

/// <summary>
/// 宠物动作引擎:区间往返行走(纯逻辑,不依赖任何 UI 框架)。
/// 由前端时钟按帧调用 <see cref="Update"/> 驱动,走到边界自动掉头,
/// 掉头时触发 <see cref="DirectionChanged"/> 供前端做镜像翻转。
/// 悬浮窗 / 宠物小屋共用。
/// </summary>
public sealed class PetWalker
{
    /// <summary>行走范围边界(像素坐标)。</summary>
    public double Min { get; set; }

    /// <summary>行走范围边界(像素坐标)。</summary>
    public double Max { get; set; }

    /// <summary>垂直范围边界(像素坐标)。引擎仍只往返驱动 X 轴,
    /// Y 轴由前端按目标点平滑移动(宠物小屋在区域内上下走),悬浮/安卓形态保持 0 即可。</summary>
    public double MinY { get; set; }

    /// <summary>垂直范围边界(像素坐标)。</summary>
    public double MaxY { get; set; }

    /// <summary>行走速度(像素/秒)。</summary>
    public double Speed { get; set; } = 120;

    /// <summary>当前位置。</summary>
    public double Position { get; set; }

    /// <summary>当前朝向,true=向右。</summary>
    public bool FacingRight { get; private set; } = true;

    /// <summary>掉头时触发,参数 true=向右。</summary>
    public event Action<bool>? DirectionChanged;

    /// <summary>按帧推进:位置 = 当前位置 + 速度 × 时间差。</summary>
    public void Update(TimeSpan delta)
    {
        var step = Speed * delta.TotalSeconds;
        var next = Position + (FacingRight ? step : -step);

        if (next >= Max)
        {
            next = Max;
            SetFacing(false);
        }
        else if (next <= Min)
        {
            next = Min;
            SetFacing(true);
        }

        Position = next;
    }

    /// <summary>
    /// 恢复位置与朝向(形态切换时保持宠物连续)。
    /// 朝向变化会触发 <see cref="DirectionChanged"/>,前端据此同步镜像。
    /// </summary>
    public void Restore(double position, bool facingRight)
    {
        Position = position;
        if (FacingRight != facingRight)
            SetFacing(facingRight);
    }

    /// <summary>从范围中点启动,初始向右。范围无效(Min &gt; Max)时返回 false。</summary>
    public bool StartAtCenter()
    {
        if (Max <= Min)
            return false;

        Position = (Min + Max) / 2;
        FacingRight = true;
        return true;
    }

    private void SetFacing(bool facingRight)
    {
        if (FacingRight == facingRight)
            return;

        FacingRight = facingRight;
        DirectionChanged?.Invoke(facingRight);
    }
}
