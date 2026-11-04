namespace SubSsh.Core.Abstractions;

/// <summary>
/// OAuth 公共组件的全量接口（对应原版 internal/oauthserver）。
/// 扩展 ServiceAbstractions.cs 中的骨架 IOAuthService；本文件不改写 ServiceAbstractions.cs。
/// 由 SubSsh.Plugin.OAuth 实现，Plugin.WebApi（或任一 HTTP 宿主）按端点调用这些方法。
/// </summary>
public interface IOAuthServer : IOAuthService
{
    /// <summary>解析请求的对外基地址：优先 PublicURL，否则由请求 scheme/host 推导（原版 Server.BaseURL）。</summary>
    string BaseUrl(OAuthRequestContext request);

    /// <summary>MCP 资源地址与受保护资源元数据地址（原版 Server.ResourceURLs）。</summary>
    (string Resource, string MetadataUrl) ResourceUrls(OAuthRequestContext request);

    /// <summary>GET /.well-known/oauth-authorization-server（RFC 8414）。</summary>
    OAuthResponse AuthorizationServerMetadata(OAuthRequestContext request);

    /// <summary>GET /.well-known/oauth-protected-resource/mcp（RFC 9728）。</summary>
    OAuthResponse ProtectedResourceMetadata(OAuthRequestContext request);

    /// <summary>POST /oauth/register 动态客户端注册（RFC 7591）。jsonBody 由调用方原样传入（限 64KiB）。</summary>
    Task<OAuthResponse> RegisterClientAsync(string jsonBody, CancellationToken ct = default);

    /// <summary>GET /oauth/authorize 授权信息页数据（客户端名、主机列表、工具组）。</summary>
    Task<OAuthResponse> AuthorizationInfoAsync(OAuthRequestContext request, IReadOnlyDictionary<string, IReadOnlyList<string>> query, CancellationToken ct = default);

    /// <summary>POST /oauth/authorize 授权决策（approve/deny），返回 redirect_uri。</summary>
    Task<OAuthResponse> AuthorizationDecisionAsync(OAuthRequestContext request, string jsonBody, CancellationToken ct = default);

    /// <summary>POST /oauth/token：authorization_code（PKCE S256）与 refresh_token 两种 grant。
    /// form 由调用方解析 application/x-www-form-urlencoded 后传入（限 64KiB）。</summary>
    Task<OAuthResponse> TokenAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default);
}

/// <summary>OAuth 请求上下文（框架无关，宿主从 HttpRequest 提取）。</summary>
public sealed record OAuthRequestContext(string Scheme, string Host, string? ForwardedProto);

/// <summary>OAuth 处理结果：状态码、响应头与 JSON 体（对象，由宿主用 camelCase/字面量序列化）。</summary>
public sealed class OAuthResponse
{
    public int Status { get; init; } = 200;
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>待序列化 JSON（字段名已与原版 json tag 一致，宿主原样输出）。</summary>
    public object? Json { get; init; }
}

/// <summary>OAuth 客户端（对齐原版 store.OAuthClient）。</summary>
public sealed record OAuthClient(
    string ClientId,
    string ClientName,
    string ClientUri,
    IReadOnlyList<string> RedirectUris,
    long CreatedAt);

/// <summary>建议 Db 插件实现的 OAuth 存取方法清单（当前由 OAuth 插件用最小 SQL 直接实现）：
/// CreateOAuthClient / GetOAuthClient / CreateOAuthAuthorizationCode /
/// ExchangeOAuthAuthorizationCode / RotateOAuthRefreshToken / ValidateHostIDs / ListHostViews。
/// 表结构来自原版 migration 0004_oauth.sql + 0005_oauth_code_tombstone.sql。</summary>
public interface IOAuthStoreExtension
{
    Task<OAuthClient> CreateOAuthClientAsync(OAuthClient client, CancellationToken ct = default);
    Task<OAuthClient> GetOAuthClientAsync(string clientId, CancellationToken ct = default);
    Task CreateOAuthAuthorizationCodeAsync(OAuthAuthorizationCode code, CancellationToken ct = default);
    /// <summary>原子交换授权码。返回 null 表示协议级拒绝（invalid_grant），
    /// 抛 <see cref="OAuthReuseException"/> 表示授权码重放（已撤销整个 grant）。</summary>
    Task<OAuthAuthorizationCode> ExchangeOAuthAuthorizationCodeAsync(OAuthAuthorizationCodeExchange input, CancellationToken ct = default);
    /// <summary>轮换刷新令牌。语义同上：null → invalid_grant，重放 → OAuthReuseException。</summary>
    Task<OAuthRefreshToken> RotateOAuthRefreshTokenAsync(OAuthRefreshTokenRotation input, CancellationToken ct = default);
    Task<IReadOnlyList<long>> ValidateHostIdsAsync(IReadOnlyList<long> hostIds, CancellationToken ct = default);
    Task<IReadOnlyList<OAuthHostView>> ListHostViewsAsync(CancellationToken ct = default);
}

/// <summary>授权码记录（对齐原版 store.OAuthAuthorizationCode）。</summary>
public sealed record OAuthAuthorizationCode
{
    public required string CodeHash { get; init; }
    public required string ClientId { get; init; }
    public required string RedirectUri { get; init; }
    public required string Resource { get; init; }
    public required string CodeChallenge { get; init; }
    public required string Scope { get; init; }
    public bool AllHosts { get; init; }
    public bool ManageHosts { get; init; }
    public long[] HostIds { get; set; } = [];
    public string[] DisabledTools { get; set; } = [];
    public required long ExpiresAt { get; init; }
    public long CreatedAt { get; set; }
    public long? UsedAt { get; set; }
    public string? GrantId { get; set; }
}

/// <summary>授权码交换入参（对齐原版 store.OAuthAuthorizationCodeExchange）。</summary>
public sealed record OAuthAuthorizationCodeExchange
{
    public required string CodeHash { get; init; }
    public required string ClientId { get; init; }
    public required string RedirectUri { get; init; }
    public required string Resource { get; init; }
    public required string CodeChallenge { get; init; }
    public required string GrantId { get; init; }
    public required string AccessTokenName { get; init; }
    public required string AccessTokenHash { get; init; }
    public required string RefreshTokenHash { get; init; }
    public required long Now { get; init; }
    public required long AccessExpiresAt { get; init; }
    public required long RefreshExpiresAt { get; init; }
}

/// <summary>刷新令牌记录（对齐原版 store.OAuthRefreshToken）。</summary>
public sealed record OAuthRefreshToken
{
    public required string TokenHash { get; init; }
    public required string GrantId { get; init; }
    public long AccessTokenId { get; init; }
    public required string ClientId { get; init; }
    public required string Resource { get; init; }
    public required string Scope { get; init; }
    public bool AllHosts { get; init; }
    public bool ManageHosts { get; init; }
    public long[] HostIds { get; init; } = [];
    public string[] DisabledTools { get; init; } = [];
    public required long ExpiresAt { get; init; }
    public long CreatedAt { get; init; }
    public long? UsedAt { get; set; }
    public long? RevokedAt { get; init; }
}

/// <summary>刷新令牌轮换入参（对齐原版 store.OAuthRefreshTokenRotation）。</summary>
public sealed record OAuthRefreshTokenRotation
{
    public required string TokenHash { get; init; }
    public required string ClientId { get; init; }
    public required string Resource { get; init; }
    public required string AccessTokenName { get; init; }
    public required string AccessTokenHash { get; init; }
    public required string RefreshTokenHash { get; init; }
    public required long Now { get; init; }
    public required long AccessExpiresAt { get; init; }
    public required long RefreshExpiresAt { get; init; }
}

/// <summary>授权页主机视图（对齐原版 store.HostView 的 JSON 字段）。</summary>
public sealed record OAuthHostView(
    long Id,
    string Name,
    string Addr,
    int Port,
    string Username,
    string AuthType,
    long? KeyId,
    string? HostkeyFp,
    long? JumpHostId,
    bool MonitorEnabled,
    IReadOnlyList<string> Tags,
    long CreatedAt);

/// <summary>授权码/刷新令牌重放：整个 grant 已被撤销，客户端必须重新走授权流程。</summary>
public sealed class OAuthReuseException(string message) : Exception(message);
