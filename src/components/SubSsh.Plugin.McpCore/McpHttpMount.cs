// /mcp HTTP handler 挂载点（脚手架）。
// 宿主接线：Plugin.WebApi 在 Kestrel 上挂 POST /mcp，把请求头与请求体交给 HandleAsync 即可；
// 资源标识（resource）与受保护资源元数据 URL（metadataUrl）由 WebApi/OAuth 侧按请求解析后传入。
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.McpCore;

/// <summary>传输无关的入站请求形状（只携带 /mcp 处理所需字段）。</summary>
public sealed record McpHttpRequest
{
    /// <summary>原始 Authorization 头（可空）。</summary>
    public string? AuthorizationHeader { get; init; }
    /// <summary>JSON-RPC 消息正文。</summary>
    public string Body { get; init; } = "";
    /// <summary>OAuth resource 标识（FindTokenForResource 的匹配值；manual 令牌豁免）。</summary>
    public string Resource { get; init; } = "";
    /// <summary>WWW-Authenticate 里回填的受保护资源元数据 URL。</summary>
    public string MetadataUrl { get; init; } = "";
}

/// <summary>出站响应形状：状态码 +（可空）正文 + 响应头。</summary>
public sealed record McpHttpResponse(
    int StatusCode, string? Body, IReadOnlyDictionary<string, string> Headers);

public sealed class McpHttpMount(
    McpServerCore server,
    BearerAuthenticator auth,
    Func<McpCallContext?, CancellationToken, Task<DisabledGroups>>? resolveDisabled = null) : IMcpHttpHandler
{
    private readonly Func<McpCallContext?, CancellationToken, Task<DisabledGroups>> _resolve =
        resolveDisabled ?? server.ResolveDisabledAsync;

    /// <summary>
    /// 处理一次 POST /mcp（对应 Go Bearer 中间件 + mcp HTTP handler）：
    /// Bearer 校验失败 → 401 + WWW-Authenticate；JSON-RPC 通知 → 202 Accepted 无正文；
    /// 其余 → 200 application/json。TODO：streamable-http 的 SSE 通道与 GET 建流后续接线。
    /// </summary>
    public async Task<McpHttpResponse> HandleAsync(McpHttpRequest request, CancellationToken ct = default)
    {
        McpPrincipal principal;
        try
        {
            principal = await auth.AuthenticateAsync(request.AuthorizationHeader, request.Resource, request.MetadataUrl, ct)
                .ConfigureAwait(false);
        }
        catch (McpUnauthorizedException ex)
        {
            return new McpHttpResponse(401, "unauthorized",
                new Dictionary<string, string> { ["WWW-Authenticate"] = ex.Challenge });
        }

        var context = principal.ToCallContext();
        var response = await server.HandleMessageAsync(request.Body, context, ct).ConfigureAwait(false);
        if (response.Length == 0)
            return new McpHttpResponse(202, null, new Dictionary<string, string>());
        return new McpHttpResponse(200, response,
            new Dictionary<string, string> { ["Content-Type"] = "application/json" });
    }

    /// <summary>当前禁用组下的 tools 列表（宿主调试 / 后续 SSE 用）。</summary>
    public Task<DisabledGroups> ResolveDisabledAsync(McpCallContext? context, CancellationToken ct) =>
        _resolve(context, ct);

    /// <summary>Core 抽象面适配：供 McpHttp 等传输插件经 IMcpHttpHandler 复用（不跨插件引用本程序集类型）。</summary>
    async Task<McpHttpReply> IMcpHttpHandler.HandleAsync(McpHttpCall request, CancellationToken ct)
    {
        var r = await HandleAsync(new McpHttpRequest
        {
            AuthorizationHeader = request.AuthorizationHeader,
            Body = request.Body,
            Resource = request.Resource,
            MetadataUrl = request.MetadataUrl,
        }, ct).ConfigureAwait(false);
        return new McpHttpReply(r.StatusCode, r.Body, r.Headers);
    }
}
