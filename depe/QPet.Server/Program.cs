using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using QPet.Core;

namespace QPet.Server;

/// <summary>
/// QPet 独立服务器入口。
/// 用法:QPet.Server [--db <path>] [--port <n>] [--admin-password <p>]
///   --db             数据库路径(默认 exe 旁 qpet.db,没有则向上找 data\qpet.db;迁移旧数据时指定旧库)
///   --port           IM 端口(默认 54321)
///   --admin-password 管理密码(默认从 settings.json 读,无则生成随机密码并落盘)
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        // 输出统一 UTF-8:避免重定向到文件/其他终端时中文乱码(默认走系统 GBK 代码页)
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // ---- 参数解析 ----
        // --db 相对路径锚定 exe 目录(如 data\qpet.db),与启动时的工作目录无关;
        // 绝对路径(迁移旧库)原样使用。
        var rawDb = Arg(args, "--db");
        var dbPath = rawDb is null
            ? ResolveDefaultDb()
            : Path.GetFullPath(Path.IsPathRooted(rawDb) ? rawDb : Path.Combine(AppContext.BaseDirectory, rawDb));
        var port = int.TryParse(Arg(args, "--port"), out var p) ? p : SyncServer.Port;
        var adminPassword = Arg(args, "--admin-password") ?? LoadOrCreateAdminPassword();

        var dbDir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dbDir))
            Directory.CreateDirectory(dbDir);

        // 端口占用预检:端口被占是唯一常见启动失败,尽早报错退出
        using (var probe = new TcpListener(IPAddress.Any, port))
        {
            try { probe.Start(); }
            catch (SocketException)
            {
                Console.Error.WriteLine($"端口 {port} 已被占用,启动失败(用 --port 换端口)。");
                return;
            }
        }

        // ---- 启动组件 ----
        var chatHub = new ChatHub(dbPath);
        SyncServer? syncServer = null;
        var adminApi = new AdminApi(chatHub, adminPassword, (uid, reason) => syncServer?.KickUser(uid, reason),
            () => syncServer?.Endpoint ?? "");
        syncServer = new SyncServer(chatHub, adminApi, port);

        try
        {
            syncServer.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"启动失败:{ex.Message}(端口 {port} 被占用?)");
            return;
        }

        // UDP 组播自动发现:回复 "QPET_SERVER ip:port" 给局域网内的客户端
        ServerDiscovery.StartListener(() => $"{syncServer.LocalIp}:{port}");

        // ---- 控制台信息 ----
        Console.WriteLine();
        Console.WriteLine("================ QPet 服务器 ================");
        Console.WriteLine($"  IP 地址:      {syncServer.LocalIp}");
        Console.WriteLine($"  连接地址:     {syncServer.Endpoint}");
        Console.WriteLine($"  管理密码:     {adminPassword}  (管理窗口登录用;可改 settings.json)");
        Console.WriteLine($"  数据文件:     {dbPath}");
        Console.WriteLine("==============================================");
        // Ctrl+C 不退出(防误触,服务器常驻);窗口叉(CTRL_CLOSE)走系统默认直接终止
        SetConsoleCtrlHandler(OnCtrlEvent, true);

        await Task.Delay(Timeout.Infinite);
    }

    private const uint CTRL_C_EVENT = 0;
    private const uint CTRL_BREAK_EVENT = 1;

    /// <summary>控制台事件处理:返回 true = 已处理(不退出),返回 false = 交给系统默认(终止进程)。</summary>
    private static bool OnCtrlEvent(uint type)
    {
        if (type == CTRL_C_EVENT || type == CTRL_BREAK_EVENT)
            return true; // 吞掉 Ctrl+C / Ctrl+Break,服务器继续跑
        return false;    // 窗口叉 / 注销 / 关机:默认终止(数据即时落库,无需清理)
    }

    // 委托必须被字段持有,防止被 GC 回收后 Ctrl+C 处理失效
    private static readonly ConsoleCtrlHandler CtrlHandler = OnCtrlEvent;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool ConsoleCtrlHandler(uint dwCtrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);

    /// <summary>
    /// 默认数据库路径:exe 旁的 qpet.db 优先(便携部署,库随 exe 走);
    /// 没有则从 exe 目录逐级向上找 data\qpet.db(开发布局 bin\Debug\net10.0 → 命中项目 data 目录,避免误开输出目录里的陈旧空库);
    /// 都没有仍在 exe 旁新建空库。
    /// </summary>
    private static string ResolveDefaultDb()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "qpet.db");
        if (File.Exists(beside))
            return beside;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data", "qpet.db");
            if (File.Exists(candidate))
                return candidate;
        }
        return beside;
    }

    /// <summary>取命令行参数值(--name value 形式)。</summary>
    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }
        return null;
    }

    /// <summary>管理密码:settings.json 有则读,没有则生成随机密码并写入。</summary>
    private static string LoadOrCreateAdminPassword()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        try
        {
            if (File.Exists(settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                // 属性名大小写不敏感:序列化走 camelCase(adminPassword),避免重启后读不到旧密码
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "AdminPassword", StringComparison.OrdinalIgnoreCase) &&
                        prop.Value.GetString() is { Length: > 0 } existing)
                        return existing;
                }
            }
        }
        catch
        {
            // settings.json 损坏:重新生成
        }

        // 随机 10 位字母数字(避免 0/O 混淆,排除易混字符)
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
        var password = new string(Enumerable.Range(0, 10)
            .Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());

        try
        {
            var json = new { AdminPassword = password };
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(json, JsonOpts.Web));
        }
        catch
        {
            // 写失败(只读目录等):密码只在控制台显示
        }
        return password;
    }
}
