using System.Globalization;

namespace QPet.Core;

/// <summary>
/// 平台设置存储:由各端实现,Win 存 JSON 文件、安卓存系统 Preferences。
/// </summary>
public interface ISettingsStore
{
    /// <summary>读取键值,不存在返回 null。</summary>
    string? Read(string key);

    /// <summary>写入键值。</summary>
    void Write(string key, string value);
}

/// <summary>类型化读写扩展(统一用固定文化,不受系统区域设置影响)。</summary>
public static class SettingsStoreExtensions
{
    public static double ReadDouble(this ISettingsStore store, string key, double fallback) =>
        double.TryParse(store.Read(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public static bool ReadBool(this ISettingsStore store, string key, bool fallback) =>
        bool.TryParse(store.Read(key), out var v) ? v : fallback;
}
