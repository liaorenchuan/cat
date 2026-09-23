using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace QPet.Core;

/// <summary>
/// 离线缓存文件存取:每账号一个 cache-{账号}.json(exe 同目录,与 settings.json 一致)。
/// 序列化走 JsonOpts.Web(camelCase);写盘用临时文件 + 改名,避免半写状态。
/// </summary>
public sealed class LocalCacheStore
{
    private readonly string _baseDir;
    private const int MaxCachedMessages = 5000; // 与 ChatView.MaxMessages 对齐

    public LocalCacheStore(string baseDir) => _baseDir = baseDir;

    /// <summary>账号 → 文件名(cache-账号.json,过滤非法文件名字符)。</summary>
    private static string FileNameFor(string account)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            account = account.Replace(c.ToString(), "_");
        return $"cache-{account}.json";
    }

    /// <summary>读取某账号缓存;文件不存在 / JSON 损坏 / 身份为空 → null(由调用方提示先联网登录)。</summary>
    public LocalCache? Load(string account)
    {
        var path = Path.Combine(_baseDir, FileNameFor(account));
        try
        {
            if (!File.Exists(path)) return null;
            var cache = JsonSerializer.Deserialize<LocalCache>(File.ReadAllText(path), JsonOpts.Web);
            if (cache is null || cache.MyUser.UserId.Length == 0) return null;
            if (cache.Messages.Count > MaxCachedMessages)
                cache.Messages.RemoveRange(0, cache.Messages.Count - MaxCachedMessages);
            return cache;
        }
        catch
        {
            return null; // 损坏缓存:按"没有缓存"处理,不崩溃
        }
    }

    /// <summary>落盘(原子写:先写 *.tmp 再改名覆盖)。返回是否写入成功 ——
    /// 调用方要靠它决定"这次会话算不算已保存",以前吞掉异常会让失败被当成成功。</summary>
    public bool Save(string account, LocalCache cache)
    {
        var path = Path.Combine(_baseDir, FileNameFor(account));
        var tmp = path + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions(JsonOpts.Web) { WriteIndented = true });
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch
        {
            // 写失败(只读目录/磁盘满):离线缓存尽力而为,不影响主流程
            return false;
        }
    }

    /// <summary>删除某账号缓存(备用)。</summary>
    public void Delete(string account)
    {
        var path = Path.Combine(_baseDir, FileNameFor(account));
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
