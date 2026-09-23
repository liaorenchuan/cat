// 管理端裁剪版:只保留用到的端口常量。Admin 走 REST(/api/admin/*),不参与 WS 帧协议,
// 协议常量与 WsFrame 以 Server / QPet.Wpf / QPet.Mobile 的完整版为准。
namespace QPet.Core;

/// <summary>跨端协议常量(Admin 仅用 DefaultPort;WS 帧协议值见三端完整版)。</summary>
public static class Protocol
{
    /// <summary>IM 服务器默认端口。</summary>
    public const int DefaultPort = 54321;
}
