using System.Windows.Controls;
using System.Windows.Media.Imaging;
using QPet.Core;

namespace QPet.Wpf;

/// <summary>
/// 宠物视觉效果接线:主图加载 + 自然眨眼 + 情绪状态控制器,悬浮窗 / 宠物小屋共用。
/// 宿主在 Loaded 后创建并传入 onEnter/onExit,协调走动(动作态进入 / 恢复常态)。
/// 主图必须先加载:BlinkLoop / PetMoodController 都以主图作常态帧。
/// </summary>
internal sealed class PetEffects
{
    private readonly Image _petImage;
    private readonly Action _onEnter; // 动作态进入(宿主:按动作开始走动或站住)
    private readonly Action _onExit;  // 恢复常态(宿主:停走动 + 恢复眨眼)

    public PetEffects(Image petImage, Action onEnter, Action onExit)
    {
        _petImage = petImage;
        _onEnter = onEnter;
        _onExit = onExit;
        LoadPetImage();
        Blink = new BlinkLoop(petImage);
        Mood = new PetMoodController(petImage,
            onEnter: () =>
            {
                Blink.Stop(); // 先停眨眼恢复常态帧,再显示动作帧,避免动作帧被眨眼停止逻辑覆盖
                _onEnter();
            },
            onExit: () => _onExit());
    }

    /// <summary>自然眨眼循环(宿主按可见性 Start/Stop)。</summary>
    public BlinkLoop Blink { get; }

    /// <summary>情绪状态控制器(宿主 Enter/Exit 触发动作)。</summary>
    public PetMoodController Mood { get; }

    /// <summary>加载宠物主图(共用素材,嵌入在 QPet.Core 程序集)。</summary>
    public void LoadPetImage()
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = PetAssets.OpenPetImage();
        bmp.CacheOption = BitmapCacheOption.OnLoad; // 立即读完流,避免延迟加载时流已释放
        bmp.EndInit();
        _petImage.Source = bmp;
    }
}
