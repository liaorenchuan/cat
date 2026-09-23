using System.Net;
using System.Net.Sockets;
using System.Text;

namespace QPet.Core;

/// <summary>
/// 局域网服务器自动发现(UDP 组播)。
/// 服务端:加入组播组监听,收到 "QPET_DISCOVER" 查询 → 单播回复 "QPET_SERVER {ip} {port}"。
/// 客户端:向组播组发查询 → 限时收集回复 → 返回候选服务器地址列表("ip:port")。
/// 组播在部分网络(AP 隔离 / 防火墙)不可用 → 客户端提供手动输入地址兜底。
/// </summary>
public static class ServerDiscovery
{
    public const string Group = "239.255.44.11";
    public const int Port = 54320;

    private const string Query = "QPET_DISCOVER";
    private const string Prefix = "QPET_SERVER ";

    private static Socket? _listener;
    private static CancellationTokenSource? _cts;

    // ---- 客户端 ----

    /// <summary>发起发现,限时收集回复,返回 "ip:port" 列表(可能为空 = 网络不支持组播)。</summary>
    public static async Task<List<string>> FindAsync(TimeSpan? timeout = null)
    {
        var results = new List<string>();
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.Bind(new IPEndPoint(IPAddress.Any, 0)); // 随机本地端口,只收单播回复
            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            try { sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true); } catch { }

            var data = Encoding.UTF8.GetBytes(Query);
            await sock.SendToAsync(data, SocketFlags.None, new IPEndPoint(IPAddress.Parse(Group), Port));

            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
            var buf = new byte[512];
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var recv = await sock.ReceiveFromAsync(
                        buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);
                    var text = Encoding.UTF8.GetString(buf, 0, recv.ReceivedBytes);
                    if (text.StartsWith(Prefix, StringComparison.Ordinal))
                    {
                        var addr = text[Prefix.Length..].Trim();
                        if (addr.Length > 0 && !results.Contains(addr))
                            results.Add(addr);
                    }
                }
                catch (OperationCanceledException)
                {
                    break; // 限时结束
                }
                catch (SocketException)
                {
                    // 坏包:跳过继续收
                }
            }
        }
        catch
        {
            // 组播不可用(权限/网络):返回空列表,调用方落手动输入
        }
        return results;
    }

    // ---- 服务端 ----

    /// <summary>开始监听组播查询;收到查询回复 serverAddress() 的值。进程退出前调用 StopListener。</summary>
    public static void StartListener(Func<string> serverAddress)
    {
        StopListener();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenerLoop(serverAddress, _cts.Token));
    }

    public static void StopListener()
    {
        _cts?.Cancel();
        _cts = null;
        try { _listener?.Dispose(); } catch { }
        _listener = null;
    }

    private static async Task ListenerLoop(Func<string> serverAddress, CancellationToken ct)
    {
        try
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.Bind(new IPEndPoint(IPAddress.Any, Port));
            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            try { sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true); } catch { }
            // 加入组播组:枚举每个在线的 IPv4 接口逐个 join。
            // 单参 MulticastOption(group) 只 join 系统默认接口,启动瞬间接口表在变(虚拟网卡初始化)
            // 时可能选错 → 收不到查询;全接口 join 消除这类偶发。
            var group = IPAddress.Parse(Group);
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    try
                    {
                        sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                            new MulticastOption(group, ua.Address));
                    }
                    catch
                    {
                        // 该接口不支持组播(如部分虚拟网卡):跳过继续
                    }
                }
            }
            _listener = sock;

            var buf = new byte[512];
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var recv = await sock.ReceiveFromAsync(
                        buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
                    var text = Encoding.UTF8.GetString(buf, 0, recv.ReceivedBytes);
                    if (text != Query || recv.RemoteEndPoint is not IPEndPoint from)
                        continue;
                    // 单播回发(组播回复会被所有监听者收到,所以必须单播)
                    var reply = Encoding.UTF8.GetBytes(Prefix + serverAddress());
                    await sock.SendToAsync(reply, SocketFlags.None, from, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // 单包失败忽略,继续监听
                }
            }
            sock.Dispose();
        }
        catch
        {
            // 组播监听失败(端口被占/权限):服务器照常跑,客户端手动输入地址兜底
        }
    }
}
