using System.Globalization;

namespace QPet.Core;

/// <summary>
/// 宠物全局参数,客户端本地配置(服务器只用到尺寸校验常量,见 Server 裁剪版)。
/// 启动时调用 <see cref="Load"/> 从平台存储读取,参数修改后调用 <see cref="Save"/> 持久化。
/// </summary>
public static class PetConfig
{
    /// <summary>走动速度(像素/秒)。</summary>
    public static double WalkSpeed { get; set; } = 120;

    /// <summary>宠物宽高(像素)。</summary>
    public static double PetSize { get; set; } = 200;

    /// <summary>默认宠物尺寸(云端字段缺省值,三端一致)。</summary>
    public const double DefaultPetSize = 200;

    /// <summary>走动时左右留白边距(像素)。</summary>
    public static double EdgeMargin { get; set; } = 20;

    /// <summary>悬浮形态是否置顶。</summary>
    public static bool Topmost { get; set; } = true;

    /// <summary>启动时调用:从平台存储读取配置,缺失的键保留默认值。</summary>
    public static void Load(ISettingsStore store)
    {
        WalkSpeed = store.ReadDouble("WalkSpeed", WalkSpeed);
        PetSize = store.ReadDouble("PetSize", PetSize);
        EdgeMargin = store.ReadDouble("EdgeMargin", EdgeMargin);
        Topmost = store.ReadBool("Topmost", Topmost);
    }

    /// <summary>保存全部配置到平台存储。</summary>
    public static void Save(ISettingsStore store)
    {
        store.Write("WalkSpeed", WalkSpeed.ToString(CultureInfo.InvariantCulture));
        store.Write("PetSize", PetSize.ToString(CultureInfo.InvariantCulture));
        store.Write("EdgeMargin", EdgeMargin.ToString(CultureInfo.InvariantCulture));
        store.Write("Topmost", Topmost.ToString());
    }
}
