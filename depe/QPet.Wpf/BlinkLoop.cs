using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QPet.Core;

namespace QPet.Wpf;

/// <summary>
/// 自然眨眼常驻:随机间隔触发一次眨眼(半闭 -> 全闭 -> 半闭 -> 睁眼)。
/// 悬浮 / 窗口两形态共用,窗口可见时循环、隐藏或关闭即停。
/// 眨眼帧素材来自 QPet.Core 嵌入资源,两帧解码结果静态共享,避免双窗口重复解码。
/// </summary>
internal sealed class BlinkLoop
{
    private readonly Image _image;
    private readonly ImageSource _restFrame; // 常态帧(睁眼),窗口主图
    private CancellationTokenSource _cts = new();
    private Task? _task;

    // 眨眼两帧(半闭 / 全闭):静态缓存,两个形态窗口共用一份
    private static readonly BitmapImage HalfFrame = LoadFrame(false);
    private static readonly BitmapImage ClosedFrame = LoadFrame(true);

    public BlinkLoop(Image image)
    {
        _image = image;
        _restFrame = image.Source; // 调用方需先设置好主图
    }

    /// <summary>开始自然眨眼循环(已在运行则忽略)。</summary>
    public void Start()
    {
        if (_task is not null)
            return;

        _cts = new CancellationTokenSource();
        _task = RunAsync(_cts.Token);
    }

    /// <summary>停止眨眼并恢复常态帧(隐藏 / 关闭时调用,可再次 Start)。</summary>
    public void Stop()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource(); // 防重复 Stop 取消在已释放实例上
        _task = null;
        _image.Source = _restFrame;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // 每次等待先随机一个间隔,避免多个窗口同时眨眼
        var nextDelay = Random.Shared.Next(2500, 6001);

        try
        {
            while (true)
            {
                await Task.Delay(nextDelay, ct);
                nextDelay = Random.Shared.Next(2500, 6001);

                // 一次眨眼:半闭 -> 全闭 -> 半闭 -> 睁眼,共约 270ms
                _image.Source = HalfFrame;
                await Task.Delay(90, ct);
                _image.Source = ClosedFrame;
                await Task.Delay(90, ct);
                _image.Source = HalfFrame;
                await Task.Delay(90, ct);
                _image.Source = _restFrame;
            }
        }
        catch (OperationCanceledException)
        {
            // 窗口隐藏 / 关闭,正常退出
        }
    }

    private static BitmapImage LoadFrame(bool closed)
    {
        using var stream = PetAssets.OpenBlinkFrame(closed);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = stream;
        bmp.CacheOption = BitmapCacheOption.OnLoad; // 立即读完流,避免延迟加载时流已释放
        bmp.EndInit();
        bmp.Freeze(); // 静态共享,跨窗口安全
        return bmp;
    }
}
