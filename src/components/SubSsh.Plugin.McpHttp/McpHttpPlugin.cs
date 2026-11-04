// SubSsh.Plugin.McpHttp — MCP streamable-http 传输插件（可插拔、可任意替换）。
// GPL-3.0。自宿独立端口的 Kestrel（默认 Listen+1，可用 ONESSH_MCP_LISTEN 覆盖）：
// - POST /mcp：复用 McpCore 的 IMcpHttpHandler（Bearer 401+WWW-Authenticate、通知 202、JSON-RPC 200）
// - GET  /mcp：本实现仅支持无状态 POST，SSE 建流返回 405
// - GET /.well-known/oauth-protected-resource[/-mcp]：IOAuthService 在位时出 RFC 9728 元数据
// 依赖：Require IMcpServer + ISettings + IMcpHttpHandler（必需）、IOAuthService（可选，上线自动补元数据端点）。
// 不跨插件引用：HTTP 语义全部经 Core 抽象（IMcpHttpHandler/McpHttpCall/McpHttpReply）。
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;

namespace SubSsh.Plugin.McpHttp;

[Plugin("mcp-http", "MCP streamable-http 传输：自宿独立端口 Kestrel，POST /mcp（Bearer 校验复用 mcp-core 的 handle 语义），可选 RFC 9728 元数据端点")]
public sealed class McpHttpPlugin : IComponent
{
    public string Name => "mcp-http";

    private ISettings? _settings;
    private IMcpHttpHandler? _handler;
    private IOAuthService? _oauth;
    private WebApplication? _app;
    private string _listen = "";

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        ctx.Require<ISettings>(s => { _settings = s; TryBuild(ctx); },
            () => { _settings = null; TryStop(ctx); });
        ctx.Require<IMcpServer>(server => { }, () => { }); // 硬依赖声明：MCP 分发面必须在位（handle 能力经 IMcpHttpHandler 使用）
        ctx.Require<IMcpHttpHandler>(h => { _handler = h; TryBuild(ctx); },
            () => { _handler = null; TryStop(ctx); });
        ctx.Require<IOAuthService>(o => { _oauth = o; },
            () => { _oauth = null; }); // 元数据端点在请求时惰性取 _oauth，无需重建服务器
        return Task.CompletedTask;
    }

    private void TryBuild(IContext ctx)
    {
        if (_app != null || _settings == null || _handler == null) return;

        var settings = _settings;
        var handler = _handler;
        _listen = ResolveListen(settings);
        var baseUrl = ResolveBaseUrl(settings);
        var resource = baseUrl + "/mcp";
        var metadataUrl = baseUrl + "/.well-known/oauth-protected-resource/mcp";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://{_listen}");
        var app = builder.Build();

        // POST /mcp：全部语义（Bearer 校验/401/202/JSON-RPC 分发）交给 mcp-core 的挂载点
        app.MapPost("/mcp", async (HttpContext http) =>
        {
            string body;
            using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
                body = await reader.ReadToEndAsync(http.RequestAborted).ConfigureAwait(false);
            var reply = await handler.HandleAsync(new McpHttpCall
            {
                AuthorizationHeader = http.Request.Headers.Authorization.ToString(),
                Body = body,
                Resource = resource,
                MetadataUrl = metadataUrl,
            }, http.RequestAborted).ConfigureAwait(false);
            http.Response.StatusCode = reply.StatusCode;
            foreach (var (k, v) in reply.Headers)
                http.Response.Headers[k] = v;
            if (reply.Body != null)
                await http.Response.WriteAsync(reply.Body, http.RequestAborted).ConfigureAwait(false);
        });

        // GET /mcp：无状态 POST 实现，SSE 建流不支持（对应 streamable-http 的 405 语义）
        app.MapGet("/mcp", (HttpContext http) =>
        {
            http.Response.StatusCode = 405;
            http.Response.ContentType = "application/json";
            return http.Response.WriteAsync(
                "{\"error\":\"SSE 建流未启用：本实现仅支持无状态 POST /mcp\"}", http.RequestAborted);
        });

        // RFC 9728 受保护资源元数据（OAuth 插件在位时；否则 404）
        app.MapGet("/.well-known/oauth-protected-resource", () => MetadataOut());
        app.MapGet("/.well-known/oauth-protected-resource/mcp", () => MetadataOut());

        ctx.Track(
            apply: () => app.StartAsync().GetAwaiter().GetResult(),
            revert: () => StopApp(app));
        _app = app;

        IResult MetadataOut() => _oauth == null
            ? Results.Json(new { error = "OAuth 组件未上线" }, statusCode: 404)
            : Results.Json(_oauth.ProtectedResourceMetadata());
    }

    private void TryStop(IContext ctx)
    {
        if (_app == null) return;
        var app = _app;
        _app = null; // 卸载时 Track 的 revert 再见到 null 即跳过
        StopApp(app);
    }

    private static void StopApp(WebApplication? app)
    {
        if (app == null) return;
        try { app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).GetAwaiter().GetResult(); } catch { }
        try { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
    }

    /// <summary>监听地址：ONESSH_MCP_LISTEN 优先；否则复用主监听的 host、端口 +1（Listen+1 语义）。</summary>
    private static string ResolveListen(ISettings settings)
    {
        if (Environment.GetEnvironmentVariable("ONESSH_MCP_LISTEN") is { Length: > 0 } v) return v;
        var listen = settings.Listen;
        var idx = listen.LastIndexOf(':');
        if (idx <= 0 || !int.TryParse(listen[(idx + 1)..], out var port)) return listen;
        return $"{listen[..idx]}:{port + 1}";
    }

    /// <summary>对外 Base URL：PublicUrl 优先；未配置时用 http://监听地址 兜底（manual 令牌不校验 resource，可正常使用）。</summary>
    private static string ResolveBaseUrl(ISettings settings) =>
        settings.PublicUrl.Length > 0
            ? settings.PublicUrl.TrimEnd('/')
            : "http://" + ResolveListen(settings);
}
