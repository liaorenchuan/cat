using System.Net;
using System.Net.NetworkInformation;
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
#if ANDROID
        // Android Wi-Fi 省电策略默认丢弃入站组播帧:必须持 MulticastLock 才收得到应答
        // (真机必需;模拟器走虚拟以太网、桌面不受此限)。本文件为 Mobile 分叉版,与 Wpf 副本差异仅此 #if 段。
        Android.Net.Wifi.WifiManager.MulticastLock? mcast = null;
        try
        {
            var wifi = (Android.Net.Wifi.WifiManager?)Android.App.Application.Context
                .GetSystemService(Android.Content.Context.WifiService);
            mcast = wifi?.CreateMulticastLock("qpet_discover");
            mcast?.SetReferenceCounted(false);
            mcast?.Acquire();
        }
        catch
        {
            mcast = null; // 拿不到锁也继续(部分设备/以太网无需锁)
        }
#endif
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.Bind(new IPEndPoint(IPAddress.Any, 0)); // 随机本地端口,只收单播回复
            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            try { sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true); } catch { }

            var data = Encoding.UTF8.GetBytes(Query);
            var dest = new IPEndPoint(IPAddress.Parse(Group), Port);
            // 组播出口不能交给系统默认选择:多网卡时按接口 metric 挑,而 VMware 等虚拟网卡的组播路由
            // metric 常低于真实网卡 → 查询包全进虚拟网络,真实网卡上的服务器一个也收不到。
            // 症状:自动检测永远转圈/失败,手动填地址却正常(TCP 直连走的是另一套路由)。
            // 逐个在线 IPv4 接口显式钉住出口各发一次,与服务器端 ListenerLoop 的逐接口 join 对称。
            var sent = 0;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    IPAddress? local = null;
                    try
                    {
                        // 部分平台(如 Android)不支持读接口地址:拿不到就跳过该接口
                        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                local = ua.Address;
                                break;
                            }
                        }
                    }
                    catch { }
                    if (local is null)
                        continue;
                    try
                    {
                        // MulticastInterface 只认 int(接口地址的网络字节序):传 IPAddress 会抛
                        // ArgumentException("specified value is not valid"),整段被吞掉 = 白改。
                        var optionValue = BitConverter.ToInt32(local.GetAddressBytes(), 0);
                        sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, optionValue);
                        await sock.SendToAsync(data, SocketFlags.None, dest);
                        sent++;
                    }
                    catch
                    {
                        // 该接口不支持组播:跳下一个
                    }
                }
            }
            catch { }
            if (sent == 0)
                await sock.SendToAsync(data, SocketFlags.None, dest); // 兜底:接口枚举全不可用才退回系统默认出口

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
        finally
        {
#if ANDROID
            try { mcast?.Release(); } catch { }
#endif
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
            // 加入组播组:枚举每个在线的 IPv4 接口逐个 join(D5 与服务器/桌面副本同步)。
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
