namespace QPet.Mobile.Services;

/// <summary>
/// "记住密码"的存储(D2):走 MAUI SecureStorage(安卓 = Keystore 加密后的 SharedPreferences),
/// 不再明文写进 Preferences —— 明文密码在 root/备份/换机迁移时是直接可读的。
/// 键名仍叫 SavedPassword;老版本写在 Preferences 里的明文会在首次读取时自动搬过去并删掉。
/// SecureStorage 在个别机型/未解锁状态下会抛异常:那时宁可不记住,也不回落到明文。
/// </summary>
public static class SecurePassword
{
    private const string Key = "SavedPassword";

    /// <summary>读取记住的密码(没存过返回 "")。顺带把旧版明文迁移到加密存储。</summary>
    public static async Task<string> GetAsync()
    {
        try
        {
            var value = await SecureStorage.Default.GetAsync(Key);
            if (!string.IsNullOrEmpty(value))
                return value;
        }
        catch
        {
            // 加密存储不可用:继续往下走,至少把上一次的明文读出来(迁移会再试一次写入)
        }

        var legacy = Preferences.Default.Get<string?>(Key, null);
        if (string.IsNullOrEmpty(legacy))
            return "";
        if (await SetAsync(legacy))
        {
            Preferences.Default.Remove(Key); // 迁移成功:删掉明文副本
            return legacy;
        }
        return legacy; // 写不进加密存储:本次仍按记住的值预填,但明文也只能先留着(否则用户白记一次)
    }

    /// <summary>保存密码;返回是否真的写进了加密存储(false = 本机存不了,调用方据此把"记住密码"关掉)。</summary>
    public static async Task<bool> SetAsync(string password)
    {
        try
        {
            await SecureStorage.Default.SetAsync(Key, password);
            Preferences.Default.Remove(Key); // 清掉可能残留的明文(迁移路径)
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>清除记住的密码(取消勾选"记住密码"),加密与明文两份都清。</summary>
    public static void Remove()
    {
        try
        {
            SecureStorage.Default.Remove(Key);
        }
        catch
        {
            // 加密存储不可用:下面清明文即可
        }
        Preferences.Default.Remove(Key);
    }
}
