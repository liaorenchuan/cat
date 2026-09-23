using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace QPet.Admin.Services;

/// <summary>管理 API 调用结果:是否成功 / 是否未授权 / HTTP 状态码 / {error} 文案 / 响应文档(成功时非空)。</summary>
public sealed record AdminApiResult(bool IsSuccess, bool Unauthorized, int Status, string? Error, JsonDocument? Doc) : IDisposable
{
    /// <summary>释放响应文档(调用方可用 using var r = ... 一并释放)。</summary>
    public void Dispose() => Doc?.Dispose();
}

/// <summary>
/// 管理端 REST 客户端:统一 Bearer 认证、401 判定与 {error} 解析。
/// 错误文案一律取自响应体/调用点原值,此处不发明新文案。
/// </summary>
public sealed class AdminApiClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private string _token = "";

    /// <summary>管理 API 基地址(http://host:port,不带 /api/admin)。</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>是否已持有登录 token(未登录时列表请求直接跳过)。</summary>
    public bool HasToken => _token.Length > 0;

    /// <summary>清除 token(401 过期时调用)。</summary>
    public void ResetToken() => _token = "";

    /// <summary>登录:成功保存 token 并返回 null;密码错误返回 "管理密码错误";网络异常向上抛(调用方显示连接失败)。</summary>
    public async Task<string?> LoginAsync(string password)
    {
        using var resp = await _http.PostAsJsonAsync($"{BaseUrl}/api/admin/login", new { password });
        if (!resp.IsSuccessStatusCode)
            return "管理密码错误";
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        _token = doc.RootElement.GetProperty("token").GetString() ?? "";
        return null;
    }

    /// <summary>GET 请求(带 Bearer)。</summary>
    public Task<AdminApiResult> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);

    /// <summary>POST JSON 请求(带 Bearer)。</summary>
    public Task<AdminApiResult> PostAsync(string path, object body) => SendAsync(HttpMethod.Post, path, body);

    private async Task<AdminApiResult> SendAsync(HttpMethod method, string path, object? body)
    {
        using var req = new HttpRequestMessage(method, $"{BaseUrl}/api/admin/{path}");
        if (_token.Length > 0)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body is not null)
            req.Content = JsonContent.Create(body);

        using var resp = await _http.SendAsync(req);
        var status = (int)resp.StatusCode;
        var text = await resp.Content.ReadAsStringAsync();
        if (resp.IsSuccessStatusCode)
            return new AdminApiResult(true, false, status, null, JsonDocument.Parse(text));
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            return new AdminApiResult(false, true, status, null, null);
        // 非成功:尝试解析 {error}(解析失败时 Error 为 null,调用方按原文案兜底)
        string? error = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            error = doc.RootElement.GetProperty("error").GetString();
        }
        catch { }
        return new AdminApiResult(false, false, status, error, null);
    }
}
