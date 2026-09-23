// quit.cs — Inkm 退出工具：停止本安装目录下运行的所有 Inkm 进程
// 用途：卸载器 [UninstallRun] 调用（卸载前优雅退出）；也可双击/命令行手动运行
// 原理：以自身所在目录为基准，终止从该目录启动的 Inkm 进程
//       （服务器被杀后 mouse-daemon 会自动退出；此处一并显式停止保险）
// 优化：对齐 server/src/utils.js 的 taskkill 方案 — WQL 查询层过滤(不拉全表)、
//       taskkill /PID 批量杀、轮询确认退出(不等固定时长)，比旧版 WMI 全枚举更快
// 用法：quit.exe [-v]  （-v 输出详细信息）
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;

class QuitApp
{
    static readonly string[] TARGET_NAMES = new string[] {
        "electron.exe", "node.exe", "cloudflared.exe",
        "mouse-daemon.exe", "input-daemon.exe"
    };

    static int Main(string[] args)
    {
        bool verbose = args.Contains("-v") || args.Contains("--verbose");
        string selfDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (selfDir == null) selfDir = ".";
        selfDir = Path.GetFullPath(selfDir).TrimEnd('\\', '/') + "\\";

        Log(verbose, "[quit] 基准目录: " + selfDir);

        // 1. 定位本目录下启动的目标进程（WQL 查询层过滤，不拉全表 → 快）
        int[] pids = FindTargetPids(selfDir);
        if (pids.Length == 0)
        {
            Log(verbose, "[quit] 未发现本目录的 Inkm 进程");
            return 0;
        }

        // 2. 一条 taskkill 批量强制结束（与 server/src/utils.js 的 taskkill /F 一致）
        Log(verbose, "[quit] 停止 " + pids.Length + " 个进程: " + string.Join(", ", pids));
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = "/F " + string.Join(" ", pids.Select(p => "/PID " + p)),
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log(verbose, "[quit] taskkill 失败: " + ex.Message);
            return 1;
        }

        // 3. 轮询确认全部退出（比固定等待更快更可靠，卸载前不留句柄）
        WaitExited(pids, 3000);
        Log(verbose, "[quit] 完成，共停止 " + pids.Length + " 个进程");
        return 0;
    }

    /// 定位目标 PID：优先 WQL 过滤，失败回退全表枚举（兼容旧环境）
    static int[] FindTargetPids(string selfDir)
    {
        try
        {
            string dirLike = selfDir.Replace("'", "''") + "%";
            string names = string.Join(",", TARGET_NAMES.Select(n => "'" + n + "'"));
            string wql = "SELECT ProcessId, ExecutablePath FROM Win32_Process" +
                         " WHERE ExecutablePath LIKE '" + dirLike + "'" +
                         " AND Name IN (" + names + ")";
            using (var searcher = new ManagementObjectSearcher(wql))
            {
                return searcher.Get().Cast<ManagementObject>()
                    .Select(mo => Convert.ToInt32(mo["ProcessId"]))
                    .Where(pid => pid != Process.GetCurrentProcess().Id)
                    .ToArray();
            }
        }
        catch
        {
            // 回退：全表枚举 + 内存过滤（老逻辑）
            using (var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, Name, ExecutablePath FROM Win32_Process"))
            {
                return searcher.Get().Cast<ManagementObject>()
                    .Where(mo => IsTarget(mo, selfDir))
                    .Select(mo => Convert.ToInt32(mo["ProcessId"]))
                    .ToArray();
            }
        }
    }

    static bool IsTarget(ManagementObject mo, string selfDir)
    {
        try
        {
            string name = Convert.ToString(mo["Name"]);
            string exePath = Convert.ToString(mo["ExecutablePath"]);
            int pid = Convert.ToInt32(mo["ProcessId"]);
            if (!TARGET_NAMES.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrEmpty(exePath)) return false;
            if (!exePath.StartsWith(selfDir, StringComparison.OrdinalIgnoreCase)) return false;
            return pid != Process.GetCurrentProcess().Id;
        }
        catch { return false; }
    }

    /// 轮询确认进程全部退出（每 200ms 查一次剩余 PID，超时放弃不阻塞卸载）
    static void WaitExited(int[] pids, int timeoutMs)
    {
        int[] remaining = (int[])pids.Clone();
        int deadline = Environment.TickCount + timeoutMs;
        while (remaining.Length > 0 && Environment.TickCount < deadline)
        {
            System.Threading.Thread.Sleep(200);
            string ids = string.Join(",", remaining.Select(p => p.ToString()));
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId FROM Win32_Process WHERE ProcessId IN (" + ids + ")"))
                {
                    remaining = searcher.Get().Cast<ManagementObject>()
                        .Select(mo => Convert.ToInt32(mo["ProcessId"]))
                        .ToArray();
                }
            }
            catch { break; } // 查询异常时放弃轮询，不阻塞卸载
        }
    }

    static void Log(bool verbose, string msg)
    {
        if (verbose) Console.WriteLine(msg);
    }
}
