using System.Windows.Media;
using System.Windows.Media.Imaging;
using QPet.Core;

namespace QPet.Wpf;

/// <summary>
/// 走路帧:只用 8 图一张素材,画面方向由素材自带。
/// 朝左走显示 8 原图(画面朝左),朝右走显示 8 的水平翻转版(画面朝右),
/// 不需要整体镜像。解码结果静态共享,悬浮 / 窗口两形态共用一份。
/// </summary>
internal static class PetWalkFrames
{
    /// <summary>朝左走路帧(8 原图)。</summary>
    public static readonly ImageSource Left = Load(flip: false);

    /// <summary>朝右走路帧(8 翻转版,画面朝右)。</summary>
    public static readonly ImageSource Right = Load(flip: true);

    /// <summary>按朝向取走路帧,true=朝右。</summary>
    public static ImageSource For(bool facingRight) => facingRight ? Right : Left;

    private static ImageSource Load(bool flip)
    {
        using var stream = PetAssets.OpenWalkFrame();
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = stream;
        bmp.CacheOption = BitmapCacheOption.OnLoad; // 立即读完流,避免延迟加载时流已释放
        bmp.EndInit();
        bmp.Freeze();

        if (!flip)
            return bmp;

        // 朝右版:8 原图(朝左)水平翻转成朝右
        var flipped = new TransformedBitmap(bmp, new ScaleTransform(-1, 1, 0, 0));
        flipped.Freeze();
        return flipped;
    }
}
