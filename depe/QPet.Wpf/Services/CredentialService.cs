using System.Security.Cryptography;
using System.Text;
using QPet.Core;

namespace QPet.Wpf.Services;

/// <summary>
/// 登录凭据保存:密码经 DPAPI(CurrentUser 作用域)加密后 Base64 存 settings.json。
/// 换机器 / 换 Windows 用户无法解密 → 返回 null,由 App 清空凭据回退登录视图(不崩溃)。
/// </summary>
public sealed class CredentialService
{
    private readonly JsonSettingsStore _store;
    private const string Key = "Password"; // 密文(Base64)

    public CredentialService(JsonSettingsStore store)
    {
        _store = store;
    }

    /// <summary>保存密码(自动登录 / 手动登录成功后调用)。</summary>
    public void Save(string password)
    {
        if (string.IsNullOrEmpty(password))
            return;
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
        _store.Write(Key, Convert.ToBase64String(encrypted));
    }

    /// <summary>读取明文密码;无凭据或解密失败(换机器/损坏)返回 null。</summary>
    public string? ReadPassword()
    {
        try
        {
            var raw = _store.Read(Key);
            if (string.IsNullOrEmpty(raw))
                return null;
            var decrypted = ProtectedData.Unprotect(
                Convert.FromBase64String(raw), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null; // 密文损坏 / 非本机加密:按无凭据处理
        }
    }

    /// <summary>清除保存的密码(认证失败时调用,避免无限自动登录)。</summary>
    public void Clear() => _store.Write(Key, "");
}
