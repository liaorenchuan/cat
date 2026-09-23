using System.Globalization;

namespace QPet.Core;

/// <summary>
/// 宠物全局参数,客户端本地配置。Mobile 裁剪版(悬浮/留白是桌面形态参数,移动端无引用,
/// 仅保留实际使用的走动速度与尺寸;Wpf 全量版见 QPet.Wpf\PetConfig.cs)。
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

    /// <summary>启动时调用:从平台存储读取配置,缺失的键保留默认值。</summary>
    public static void Load(ISettingsStore store)
    {
        WalkSpeed = store.ReadDouble("WalkSpeed", WalkSpeed);
        PetSize = store.ReadDouble("PetSize", PetSize);
    }

    /// <summary>保存全部配置到平台存储。</summary>
    public static void Save(ISettingsStore store)
    {
        store.Write("WalkSpeed", WalkSpeed.ToString(CultureInfo.InvariantCulture));
        store.Write("PetSize", PetSize.ToString(CultureInfo.InvariantCulture));
    }
}
