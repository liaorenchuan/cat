using System.IO;
using System.Text.Json;

namespace QPet.Core;

/// <summary>
/// 通用设置存储:JSON 文件(exe 同目录 settings.json),便于用户直接查看修改。
/// 客户端与管理端共用(管理端用它记住服务器地址)。
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _data;

    public JsonSettingsStore(string path)
    {
        _path = path;
        _data = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new()
            : new Dictionary<string, string>();
    }

    public string? Read(string key) =>
        _data.TryGetValue(key, out var v) ? v : null;

    public void Write(string key, string value)
    {
        _data[key] = value;
        // 多进程共享同一文件(双客户端同目录):写前合并磁盘最新值再写回,只更新本键,
        // 避免用进程启动时的旧缓存全量覆盖其他进程的写入(如未读游标被回退、红点重现)。
        Dictionary<string, string>? target = null;
        try
        {
            if (File.Exists(_path))
            {
                var onDisk = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
                if (onDisk is not null)
                {
                    onDisk[key] = value;
                    target = onDisk;
                }
            }
        }
        catch
        {
            // 磁盘损坏/被占用:退回内存缓存写入(下次读取会重算兜底)
        }
        var data = target ?? _data;
        File.WriteAllText(_path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        if (target is not null)
        {
            // 同步内存缓存(只合并不替换):后续 Read 读到其他进程的最新值,
            // 本进程刚写/未落盘的键也保留
            foreach (var (k, v) in target)
                _data[k] = v;
        }
    }
}
