using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace QPet.Core;

/// <summary>
/// 局域网服务器自动发现(UDP 组播),Admin 裁剪版:只保留客户端查询侧(FindAsync),
/// 服务端监听侧(StartListener/StopListener)没有调用方,已删除。
/// 组播在部分网络(AP 隔离 / 防火墙)不可用 → 客户端提供手动输入地址兜底。
/// </summary>
public static class ServerDiscovery
{
    public const string Group = "239.255.44.11";
    public const int Port = 54320;

    private const string Query = "QPET_DISCOVER";
    private const string Prefix = "QPET_SERVER ";

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
        return results;
    }
}
