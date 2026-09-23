using System.Text.Json;

namespace QPet.Core;

/// <summary>
/// 全项目共享的 JSON 序列化选项(Web 默认:camelCase 属性名 + 反序列化大小写不敏感)。
/// 只读单例线程安全,序列化输出与原先各处 new(JsonSerializerDefaults.Web) 完全一致。
/// </summary>
public static class JsonOpts
{
    /// <summary>Web 默认选项,服务器/客户端共用。</summary>
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
