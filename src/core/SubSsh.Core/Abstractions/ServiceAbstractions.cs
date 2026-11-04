namespace SubSsh.Core.Abstractions;

/// <summary>全局设置（对应原版 internal/config/config.go；由宿主或 Plugin.Settings 提供）。</summary>
public interface ISettings
{
    string DataDir { get; }
    string Listen { get; }
    string PublicUrl { get; }
    string MasterKey { get; }
    string AdminPassword { get; }
    string EmbeddingApiUrl { get; }
    string EmbeddingApiKey { get; }
    string EmbeddingModel { get; }
    int PollInterval { get; }
    string SearchHelper { get; }
    bool McpApps { get; }
    string[] DisabledTools { get; }
}

/// <summary>数据库公共组件（骨架子集；后续按 store/store.go 扩全方法集）。</summary>
public interface ISubSshStore : IAsyncDisposable
{
    Task<bool> HealthAsync(CancellationToken ct = default);
    string ConnectionString { get; }
}

/// <summary>OAuth 公共组件（骨架；后续按 oauthserver/*.go 扩全 PKCE/动态注册）。</summary>
public interface IOAuthService
{
    string PublicUrl { get; }
    /// <summary>OAuth 2.0 Protected Resource Metadata (RFC 9728)。</summary>
    object ProtectedResourceMetadata();
    /// <summary>OAuth 2.0 Authorization Server Metadata (RFC 8414)。</summary>
    object AuthorizationServerMetadata();
}

/// <summary>Web API 公共组件：自宿 Kestrel 并托管 REST + SPA。</summary>
public interface IWebApiServer : IAsyncDisposable
{
    string Listen { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

/// <summary>业务异常，MCP 层转 isError。</summary>
public sealed class SubSshException(string message) : Exception(message);
