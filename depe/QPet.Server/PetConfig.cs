namespace QPet.Core;

/// <summary>
/// 宠物尺寸校验常量(由原 QPet.Core.PetConfig 裁剪:服务器只用到这三个常量,
/// 客户端版仍含 WalkSpeed/PetSize/EdgeMargin/Topmost 与 Load/Save)。
/// </summary>
public static class PetConfig
{
    /// <summary>默认宠物尺寸(云端字段缺省值,与客户端一致)。</summary>
    public const double DefaultPetSize = 200;

    /// <summary>缩放允许范围(下端)。</summary>
    public const double MinPetSize = 80;

    /// <summary>缩放允许范围(上端)。</summary>
    public const double MaxPetSize = 400;
}
