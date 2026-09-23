using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QPet.Core;

namespace QPet.Wpf;

/// <summary>
/// 宠物状态控制器:统一管理表情帧展示与恢复。
/// <see cref="Enter"/> 切到目标状态并显示对应帧,限时状态到时自动回到常态,
/// 常驻状态(睡觉 / 拖拽)由调用方 <see cref="Exit"/> 恢复。
/// 窗口注册 <see cref="onEnter"/> / <see cref="onExit"/> 回调来协调走动与眨眼。
/// 状态帧解码结果静态共享,两个形态窗口共用一份。
/// </summary>
internal sealed class PetMoodController
{
    /// <summary>限时状态默认时长。</summary>
    public static readonly TimeSpan HeartDuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ActionDuration = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ClickedDuration = TimeSpan.FromSeconds(1);

    private readonly Image _image;
    private readonly ImageSource _normalFrame; // 常态帧(原图,窗口主图)
    private readonly Action? _onEnter;
    private readonly Action? _onExit;
    private CancellationTokenSource _cts = new();
    private PetMood _mood = PetMood.Normal;

    // 状态帧缓存:静态共享,两个形态窗口不重复解码
    private static readonly IReadOnlyDictionary<PetMood, BitmapImage> Frames = BuildFrames();

    public PetMoodController(Image image, Action? onEnter = null, Action? onExit = null)
    {
        _image = image;
        _normalFrame = image.Source; // 调用方需先设置好主图
        _onEnter = onEnter;
        _onExit = onExit;
    }

    /// <summary>当前状态。</summary>
    public PetMood Mood => _mood;

    /// <summary>
    /// 切换到目标状态并显示对应帧。
    /// duration 为 null 表示常驻(睡觉 / 拖拽),由 <see cref="Exit"/> 恢复;
    /// 否则到时自动回到常态。重复进入相同状态时重置计时(连续点击刷新停顿)。
    /// </summary>
    public void Enter(PetMood mood, TimeSpan? duration = null)
    {
        if (_mood != mood)
        {
            // 进入任何新状态都先取消旧状态计时,否则限时状态(比心/吃面等)残留的
            // 计时器会在常驻状态(睡觉)期间触发 Exit,把宠物误唤醒
            ReplaceToken();
            _mood = mood;
            // 先停走动与眨眼(onEnter 里 blink.Stop 会把画面恢复常态帧),再显示动作帧,
            // 避免动作帧被眨眼停止逻辑覆盖
            _onEnter?.Invoke();
            if (mood != PetMood.Walk)
                _image.Source = Frames[mood];
            // 走路帧是两帧轮流动画且随方向切换,画面由窗口走动逻辑设置
        }

        // 有时长就(重新)计时:连点时取消旧计时,从最后一次进入开始算
        if (duration is { } d)
        {
            ReplaceToken();
            var ct = _cts.Token;
            _ = ResetAfterAsync(d, ct);
        }
    }

    /// <summary>恢复常态帧并触发恢复回调(睡觉唤醒 / 拖拽放下)。</summary>
    public void Exit()
    {
        if (_mood == PetMood.Normal)
            return;

        ReplaceToken();
        _mood = PetMood.Normal;
        _image.Source = _normalFrame;
        _onExit?.Invoke();
    }

    private async Task ResetAfterAsync(TimeSpan duration, CancellationToken ct)
    {
        try
        {
            await Task.Delay(duration, ct);
        }
        catch (OperationCanceledException)
        {
            return; // 已被新状态替换,由新状态负责画面
        }
        Exit();
    }

    /// <summary>取消并释放当前 CTS,立刻换一个新的,避免后续 Cancel 落在已释放实例上。</summary>
    private void ReplaceToken()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
    }

    private static IReadOnlyDictionary<PetMood, BitmapImage> BuildFrames()
    {
        var frames = new Dictionary<PetMood, BitmapImage>();
        foreach (var mood in Enum.GetValues<PetMood>())
        {
            if (mood == PetMood.Normal)
                continue; // 常态帧用窗口主图,不缓存
            if (mood == PetMood.Walk)
                continue; // 走路帧两帧轮流且分方向,由窗口走动逻辑管理

            using var stream = PetAssets.OpenMoodFrame(mood);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = stream;
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 立即读完流,避免延迟加载时流已释放
            bmp.EndInit();
            bmp.Freeze(); // 静态共享,跨窗口安全
            frames[mood] = bmp;
        }
        return frames;
    }
}
