// MCP 传输层公共抽象：把 McpCore 的 HTTP handle 能力（Bearer 401+WWW-Authenticate、
// 通知 202、JSON-RPC 分发）以 Core 接口暴露，供 McpHttp 等传输插件复用。
// 接口只放 Core，防止跨插件项目引用（CONVENTIONS「接口归属」）。
namespace SubSsh.Core.Abstractions;

/// <summary>传输无关的 /mcp 入站请求形状（对应 Plugin.McpCore 的 McpHttpRequest）。</summary>
public sealed record McpHttpCall
{
    /// <summary>原始 Authorization 头（可空）。</summary>
    public string? AuthorizationHeader { get; init; }
    /// <summary>JSON-RPC 消息正文。</summary>
    public string Body { get; init; } = "";
    /// <summary>OAuth resource 标识（令牌 resource 匹配值；manual 令牌豁免）。</summary>
    public string Resource { get; init; } = "";
    /// <summary>WWW-Authenticate 里回填的受保护资源元数据 URL。</summary>
    public string MetadataUrl { get; init; } = "";
}

/// <summary>出站响应形状：状态码 +（可空）正文 + 响应头。</summary>
public sealed record McpHttpReply(
    int StatusCode, string? Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// MCP HTTP 处理面（Provide: Plugin.McpCore，实现挂在 McpHttpMount 上）：
/// Bearer 校验失败回 401 + WWW-Authenticate；通知回 202 无正文；其余回 200 application/json。
/// </summary>
public interface IMcpHttpHandler
{
    Task<McpHttpReply> HandleAsync(McpHttpCall request, CancellationToken ct = default);
}
