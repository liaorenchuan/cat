using QPet.Core;

namespace QPet.Mobile.Services;

/// <summary>
/// 设置存储:MAUI Preferences 实现(桌面 JsonSettingsStore 的安卓对应)。
/// 只放账号/服务器地址这类非敏感项;记住的密码走 SecurePassword(系统加密存储,见 D2)。
/// </summary>
public sealed class PreferencesStore : ISettingsStore
{
    public string? Read(string key) => Preferences.Default.Get<string?>(key, null);

    public void Write(string key, string value) => Preferences.Default.Set(key, value);
}
