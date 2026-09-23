using System.IO;

namespace QPet.Core;

/// <summary>
/// 宠物素材加载:素材编译进 QPet.Wpf 程序集(EmbeddedResource LogicalName 保持
/// QPet.Core.Resources.* 与下方资源名常量一一对应,勿改名),悬浮窗/宠物小屋共用。
/// </summary>
public static class PetAssets
{
    private const string PetImageName = "QPet.Core.Resources.pet.png";
    private const string BlinkHalfName = "QPet.Core.Resources.pet_blink_half.png";
    private const string BlinkClosedName = "QPet.Core.Resources.pet_blink_closed.png";
    private const string HeartName = "QPet.Core.Resources.pet_heart.png";
    private const string KneelName = "QPet.Core.Resources.pet_kneel.png";
    private const string ClickedName = "QPet.Core.Resources.pet_clicked.png";
    private const string EatName = "QPet.Core.Resources.pet_eat.png";
    private const string SleepName = "QPet.Core.Resources.pet_sleep.png";
    private const string DragName = "QPet.Core.Resources.pet_drag.png";
    private const string LeftWalkName = "QPet.Core.Resources.pet_left.png";

    /// <summary>打开宠物图片流(调用方负责释放)。</summary>
    public static Stream OpenPetImage() => OpenEmbedded(PetImageName);

    /// <summary>打开眨眼帧图片流,true=全闭、false=半闭(常态图即 <see cref="OpenPetImage"/>)。</summary>
    public static Stream OpenBlinkFrame(bool closed) => OpenEmbedded(closed ? BlinkClosedName : BlinkHalfName);

    /// <summary>打开走路帧图片流(走路只用 8 图一张素材,镜像帧由前端翻转)。</summary>
    public static Stream OpenWalkFrame() => OpenEmbedded(LeftWalkName);

    /// <summary>打开宠物状态帧图片流(调用方负责释放),未知状态回退常态图。</summary>
    public static Stream OpenMoodFrame(PetMood mood) => mood switch
    {
        PetMood.Heart => OpenEmbedded(HeartName),
        PetMood.Kneel => OpenEmbedded(KneelName),
        PetMood.Clicked => OpenEmbedded(ClickedName),
        PetMood.Eat => OpenEmbedded(EatName),
        PetMood.Sleep => OpenEmbedded(SleepName),
        PetMood.Drag => OpenEmbedded(DragName),
        _ => OpenEmbedded(PetImageName),
    };

    private static Stream OpenEmbedded(string name)
    {
        var stream = typeof(PetAssets).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"素材 {name} 缺失");
        // 复制为可定位的内存流,方便各 UI 框架重复读取
        var copy = new MemoryStream();
        stream.CopyTo(copy);
        stream.Dispose();
        copy.Position = 0;
        return copy;
    }
}
