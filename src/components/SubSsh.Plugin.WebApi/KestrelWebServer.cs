// KestrelWebServer：自宿 Kestrel，挂载 /api/v1 全量 REST（原版 webapi/api.go Handler() 的 1:1 迁入）、
// /api/login（管理端会话，auth.go）、/api/events SSE、/api/ws/terminal WebSocket 终端代理、
// OAuth .well-known 元数据与 SPA 托管。
namespace SubSsh.Plugin.WebApi;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using SubSsh.Core.Abstractions;
using SubSsh.Core.Events;

/// <summary>自宿 Kestrel 的 Web 服务器（IWebApiServer 实现）。</summary>
public sealed class KestrelWebServer : IWebApiServer
{
    private readonly ISettings _settings;
    private WebApplication? _app;
    private ApiServices _services;

    public string Listen => _settings.Listen;

    public KestrelWebServer(ISettings settings, IWebApiStore store, IOAuthService oauth, EventBus events, ICryptoBox? crypto = null)
    {
        _settings = settings;
        _services = new ApiServices(settings, store, events);
        _services.Crypto = crypto;
        OAuth = oauth;
    }

    public IOAuthService OAuth { get; set; }

    /// <summary>Crypto 插件上线/下线时更新私钥加密器。</summary>
    public void SetCrypto(ICryptoBox? crypto) => _services.Crypto = crypto;
    public void SetMcpHandler(IMcpHttpHandler? h) => _services.Mcp = h;

    public static KestrelWebServer BuildAndStart(ISettings settings, IWebApiStore store, IOAuthService oauth, EventBus events, ICryptoBox? crypto = null)
    {
        var server = new KestrelWebServer(settings, store, oauth, events, crypto);
        server.StartAsync().GetAwaiter().GetResult();
        return server;
    }

    /// <summary>绑定可选能力服务（插件 Require 回调；下线时传 null 保持可插拔语义）。</summary>
    public void SetHostManager(IWebApiHostManager? v) => _services.Hosts = v;
    public void SetConnectionPool(IWebApiConnectionPool? v) => _services.Pool = v;
    public void SetCommandRunner(IWebApiCommandRunner? v) => _services.Exec = v;
    public void SetJobManager(IWebApiJobManager? v) => _services.Jobs = v;
    public void SetFileManager(IWebApiFileManager? v) => _services.Files = v;

    public Task StartAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://{Listen}");
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 100L << 20 + 1);
        var app = builder.Build();

        // ── 认证中间件（auth.go Require）：/api/v1/*（除登录端点）、/api/events、/api/ws/terminal 需要会话。──
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            var isLoginEndpoint =
                path == "/api/login" ||
                path == "/api/v1/login" ||            // 原版 Go mux: POST /api/v1/login
                path == "/api/v1/auth/login";         // Web 前端 client.ts 使用的登录端点
            var needsAuth =
                !isLoginEndpoint &&
                (path.StartsWith("/api/v1", StringComparison.Ordinal) ||
                 path.StartsWith("/api/events", StringComparison.Ordinal) ||
                 path.StartsWith("/api/ws/terminal", StringComparison.Ordinal));
            if (!needsAuth || _services.Auth.IsValid(_services.Auth.GetSessionCookie(ctx), DateTimeOffset.UtcNow))
            {
                await next(ctx);
                return;
            }
            ctx.Response.StatusCode = 401;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync("unauthorized", ctx.RequestAborted);
        });

        // ── 登录（auth.go Login）：POST {"password"} → Set-Cookie + {"ok":true}。
        //    三个别名：/api/login（早期实现）、/api/v1/login（原版 Go 路由）、/api/v1/auth/login（Web 前端）。──
        Task LoginHandler(HttpContext ctx) => AuthLoginAsync(ctx);
        app.MapPost("/api/login", (HttpContext ctx) => LoginHandler(ctx));
        app.MapPost("/api/v1/login", (HttpContext ctx) => LoginHandler(ctx));
        app.MapPost("/api/v1/auth/login", (HttpContext ctx) => LoginHandler(ctx));

        // ── OAuth / 健康检查（宿主级，不属于 /api/v1）──
        app.MapGet("/healthz", async (CancellationToken ct) =>
        {
            try
            {
                await _services.Store.ListHostsAsync(ct);
                return ApiRoutes.JsonOut(new { ok = true, version = "1.0-webapi-port" });
            }
            catch
            {
                return Results.StatusCode(503);
            }
        });
        app.MapGet("/.well-known/oauth-protected-resource", () => ApiRoutes.JsonOut(OAuth.ProtectedResourceMetadata()));
        app.MapGet("/.well-known/oauth-protected-resource/mcp", () => ApiRoutes.JsonOut(OAuth.ProtectedResourceMetadata()));
        // ── MCP streamable-http（由 McpCore 插件经 IMcpHttpHandler 提供；未上线时 503）──
        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            if (_services.Mcp is null)
            {
                ctx.Response.StatusCode = 503;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":\"MCP 组件未上线\"}", ctx.RequestAborted);
                return;
            }
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            var baseUri = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var call = new McpHttpCall
            {
                AuthorizationHeader = ctx.Request.Headers.Authorization.ToString(),
                Body = body,
                Resource = baseUri + "/mcp",
                MetadataUrl = baseUri + "/.well-known/oauth-protected-resource/mcp",
            };
            var reply = await _services.Mcp.HandleAsync(call, ctx.RequestAborted);
            ctx.Response.StatusCode = reply.StatusCode;
            ctx.Response.ContentType = "application/json";
            foreach (var (k, v) in reply.Headers) ctx.Response.Headers[k] = v;
            if (reply.Body != null)
                await ctx.Response.WriteAsync(reply.Body, ctx.RequestAborted);
        });
        app.MapGet("/.well-known/oauth-authorization-server", () => ApiRoutes.JsonOut(OAuth.AuthorizationServerMetadata()));

        MapApiV1(app);
        MapEvents(app);
        MapTerminal(app);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        async Task AuthLoginAsync(HttpContext ctx)
        {
            JsonDocument doc;
            try
            {
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
                doc = JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()));
            }
            catch
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync("密码错误", ctx.RequestAborted);
                return;
            }
            var password = doc.RootElement.TryGetProperty("password", out var p) ? p.GetString() : null;
            if (!_services.Auth.CheckPassword(password))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync("密码错误", ctx.RequestAborted);
                return;
            }
            _services.Auth.SetSessionCookie(ctx);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"ok\":true}", ctx.RequestAborted);
        }

        // SPA 托管：优先插件自身目录下的 dist（随插件部署），退回宿主 BaseDirectory\dist。
        // 静态文件根必须显式指向 dist（默认 WebRoot=wwwroot，插件宿主下不存在）。
        var pluginDir = Path.GetDirectoryName(typeof(KestrelWebServer).Assembly.Location);
        var dist = Path.Combine(pluginDir ?? AppContext.BaseDirectory, "dist");
        if (!Directory.Exists(dist))
            dist = Path.Combine(AppContext.BaseDirectory, "dist");
        if (Directory.Exists(dist))
        {
            var provider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(dist));
            var staticOpts = new StaticFileOptions { FileProvider = provider };
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
            app.UseStaticFiles(staticOpts);
            app.MapFallbackToFile("index.html", staticOpts);
        }
        else
        {
            app.MapFallback(() => Results.Text("SubSsh WebApi 运行中（web/dist 未构建）", "text/plain"));
        }

        _app = app;
        return app.StartAsync(ct);
    }

    /// <summary>原版 api.go Handler() 的全部路由，前缀 /api/v1，1:1。</summary>
    private void MapApiV1(WebApplication app)
    {
        var a = _services;
        app.MapGet("/api/v1/hosts", (CancellationToken ct) => ApiRoutes.HostsList(a, ct));
        app.MapPost("/api/v1/hosts", (HttpContext ctx, CancellationToken ct) => ApiRoutes.HostsCreate(a, ctx, ct));
        app.MapPut("/api/v1/hosts/{id}", (long id, HttpContext ctx, CancellationToken ct) => ApiRoutes.HostUpdate(a, id, ctx, ct));
        app.MapDelete("/api/v1/hosts/{id}", (long id, CancellationToken ct) => ApiRoutes.HostDelete(a, id, ct));
        app.MapPost("/api/v1/hosts/{id}/test", (long id, CancellationToken ct) => ApiRoutes.HostTest(a, id, ct));
        app.MapPost("/api/v1/hosts/{id}/reset-fingerprint", (long id, CancellationToken ct) => ApiRoutes.ResetFingerprint(a, id, ct));
        app.MapPost("/api/v1/hosts/{id}/archive", (long id, CancellationToken ct) => ApiRoutes.HostArchive(a, id, unarchive: false, ct));
        app.MapPost("/api/v1/hosts/{id}/unarchive", (long id, CancellationToken ct) => ApiRoutes.HostArchive(a, id, unarchive: true, ct));
        app.MapGet("/api/v1/keys", (CancellationToken ct) => ApiRoutes.KeysList(a, ct));
        app.MapPost("/api/v1/keys", (HttpContext ctx, CancellationToken ct) => ApiRoutes.KeysCreate(a, ctx, ct));
        app.MapDelete("/api/v1/keys/{id}", (long id, CancellationToken ct) => ApiRoutes.KeyDelete(a, id, ct));
        app.MapGet("/api/v1/tokens", (CancellationToken ct) => ApiRoutes.TokensList(a, ct));
        app.MapPost("/api/v1/tokens", (HttpContext ctx, CancellationToken ct) => ApiRoutes.TokensCreate(a, ctx, ct));
        app.MapPut("/api/v1/tokens/{id}", (long id, HttpContext ctx, CancellationToken ct) => ApiRoutes.TokenUpdate(a, id, ctx, ct));
        app.MapDelete("/api/v1/tokens/{id}", (long id, CancellationToken ct) => ApiRoutes.TokenDelete(a, id, ct));
        app.MapGet("/api/v1/tool-groups", () => ApiRoutes.ToolGroupsList());
        app.MapGet("/api/v1/jobs", (string? host, CancellationToken ct) => ApiRoutes.JobsList(a, host ?? "", ct));
        app.MapPost("/api/v1/jobs/{id}/kill", (string id, CancellationToken ct) => ApiRoutes.JobKill(a, id, ct));
        app.MapGet("/api/v1/jobs/{id}/logs", (HttpContext http, string id, long? offset_bytes, int? limit_bytes, CancellationToken ct)
            => ApiRoutes.JobLogs(a, http, id, offset_bytes ?? 0, limit_bytes ?? 0, ct));
        app.MapGet("/api/v1/command-runs", (HttpContext ctx, CancellationToken ct) => ApiRoutes.CommandRuns(a, ctx, ct));
        app.MapGet("/api/v1/command-runs/{id}", (HttpContext ctx, string id, CancellationToken ct) => ApiRoutes.CommandRun(a, ctx, id, ct));
        app.MapGet("/api/v1/command-runs/{id}/output", (HttpContext ctx, string id, string? stream, long? offset_bytes, int? limit_bytes, CancellationToken ct)
            => ApiRoutes.CommandRunOutput(a, ctx, id, stream ?? "", offset_bytes ?? 0, limit_bytes ?? 0, ct));
        app.MapGet("/api/v1/audit", (HttpContext ctx, CancellationToken ct) => ApiRoutes.Audit(a, ctx, ct));
        app.MapGet("/api/v1/audit/tools", (CancellationToken ct) => ApiRoutes.AuditTools(a, ct));
        app.MapGet("/api/v1/memories", (HttpContext ctx, CancellationToken ct) => ApiRoutes.Memories(a, ctx, ct));
        app.MapGet("/api/v1/memories/stats", (CancellationToken ct) => ApiRoutes.MemoryStats(a, ct));
        app.MapDelete("/api/v1/memories/{id}", (long id, CancellationToken ct) => ApiRoutes.MemoryDelete(a, id, ct));
        app.MapGet("/api/v1/metrics/{hostId}", (long hostId, int? hours, CancellationToken ct) => ApiRoutes.Metrics(a, hostId, hours ?? 0, ct));
        app.MapGet("/api/v1/sftp/{hostId}/list", (long hostId, string? path, CancellationToken ct) => ApiRoutes.SftpList(a, hostId, path ?? "", ct));
        app.MapMethods("/api/v1/sftp/{hostId}/download", ["HEAD"], (HttpContext ctx, long hostId, string? path, CancellationToken ct)
            => ApiRoutes.SftpDownload(a, ctx, hostId, path ?? "", headOnly: true, ct));
        app.MapGet("/api/v1/sftp/{hostId}/download", (HttpContext ctx, long hostId, string? path, CancellationToken ct)
            => ApiRoutes.SftpDownload(a, ctx, hostId, path ?? "", headOnly: false, ct));
        app.MapPost("/api/v1/sftp/{hostId}/upload", async (long hostId, HttpRequest request, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return ApiRoutes.ApiError("multipart form required", 400);
            var form = await request.ReadFormAsync(ct);
            var file = form.Files["file"];
            if (file is null) return ApiRoutes.ApiError("missing file field", 400);
            return await ApiRoutes.SftpUpload(a, hostId, file, form["path"].ToString(), ct);
        });
    }

    /// <summary>/api/events SSE（api.go sse）：EventBus 广播，data 为 {"type","ts","data"} JSON。</summary>
    private void MapEvents(WebApplication app)
    {
        var bus = new WebApiBus(_services.Events);
        app.MapGet("/api/events", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            using var sub = bus.Subscribe();
            var heartbeat = Task.Delay(Timeout.InfiniteTimeSpan, ctx.RequestAborted);
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                var evt = sub.TryRead();
                if (evt is null)
                {
                    try { await Task.WhenAny(Task.Delay(200, ctx.RequestAborted), heartbeat); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }
                var json = JsonSerializer.Serialize(evt, ApiRoutes.JsonOpts);
                await ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
        });
    }

    /// <summary>/api/ws/terminal（terminal.go）：WebSocket 代理到 SSH shell。
    /// TODO(port)：IWebApiConnectionPool 未就绪（Plugin.SshPool），池缺失时返回 502 JSON。</summary>
    private void MapTerminal(WebApplication app)
    {
        var a = _services;
        app.MapGet("/api/ws/terminal", async (HttpContext ctx, string? host, int? cols, int? rows) =>
        {
            if (string.IsNullOrEmpty(host))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = "host 必填" }, ApiRoutes.JsonOpts), ctx.RequestAborted);
                return;
            }
            try { await a.Store.GetHostByNameAsync(host, ctx.RequestAborted); }
            catch (Exception e)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = e.Message }, ApiRoutes.JsonOpts), ctx.RequestAborted);
                return;
            }
            if (a.Pool is null)
            {
                ctx.Response.StatusCode = 502;
                ctx.Response.ContentType = "application/json";
                // TODO(port)：池上线后走 a.Pool.OpenShellAsync(host, rows, cols)。
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = "SSH 连接池未上线（TODO port: Plugin.SshPool）" }, ApiRoutes.JsonOpts), ctx.RequestAborted);
                return;
            }
            var r = rows is int rv && rv > 0 ? rv : 30;
            var c = cols is int cv && cv > 0 ? cv : 120;
            IWebApiSshShell? shell;
            try { shell = await a.Pool.OpenShellAsync(host, r, c, ctx.RequestAborted); }
            catch (Exception e)
            {
                ctx.Response.StatusCode = 502;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = e.Message }, ApiRoutes.JsonOpts), ctx.RequestAborted);
                return;
            }
            if (shell is null)
            {
                ctx.Response.StatusCode = 502;
                return;
            }
            if (!ctx.WebSockets.IsWebSocketRequest) { await shell.DisposeAsync(); ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await using (shell)
            {
                // 出向：SSH stdout → WS binary 帧。
                var pump = Task.Run(async () =>
                {
                    while (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        var chunk = await shell.ReadOutputAsync(ctx.RequestAborted);
                        if (chunk is null || chunk.Length == 0) break;
                        var seg = new ArraySegment<byte>(chunk, 0, chunk.Length);
                        await ws.SendAsync(seg, WebSocketMessageType.Binary, true, ctx.RequestAborted);
                    }
                });
                // 入向：WS JSON 消息 {"type":"input"|"resize","data","cols","rows"}。
                var buf = new byte[64 * 1024];
                try
                {
                    while (ws.State == WebSocketState.Open && !ctx.RequestAborted.IsCancellationRequested)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ctx.RequestAborted);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.Count == 0) continue;
                        TerminalMessage? msg;
                        try { msg = JsonSerializer.Deserialize<TerminalMessage>(new ReadOnlySpan<byte>(buf, 0, result.Count), ApiRoutes.JsonOpts); }
                        catch { continue; }
                        if (msg is null) continue;
                        switch (msg.Type)
                        {
                            case "input":
                                await shell.WriteInputAsync(msg.Data ?? "", ctx.RequestAborted);
                                break;
                            case "resize":
                                if (msg.Cols > 0 && msg.Rows > 0)
                                    await shell.ResizeAsync(msg.Rows, msg.Cols, ctx.RequestAborted);
                                break;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                await pump.WaitAsync(TimeSpan.FromSeconds(2));
            }
        });
    }

    /// <summary>terminal.go terminalMessage。</summary>
    private sealed record TerminalMessage(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("data")] string? Data,
        [property: JsonPropertyName("cols")] int Cols,
        [property: JsonPropertyName("rows")] int Rows);

    public Task StopAsync(CancellationToken ct = default)
        => _app?.StopAsync(ct) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_app != null) await _app.DisposeAsync();
    }
}
