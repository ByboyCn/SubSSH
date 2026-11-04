// SubSsh.Core.Abstractions.WebApiAbstractions — WebApi 插件所需的服务接口与 DTO。
// 与原版 internal/webapi（api.go/auth.go/command_runs.go/memories.go/terminal.go）对齐；
// 字段名与 store/models.go、command_runs.go 的 json tag 一致（显式 JsonPropertyName，camelCase）。
// 注意：不要把这些接口并入 ServiceAbstractions.cs（避免改动既有骨架契约）。
namespace SubSsh.Core.Abstractions;

using System.Text.Json.Serialization;

// ── 错误分类（对应 hostmanager.ErrorKind / webapi.hostErrorStatus）──
public enum WebApiHostErrorKind { Invalid, NotFound, Conflict, Connection, Internal }

/// <summary>主机管理错误；WebApi 按 Kind 映射 HTTP 状态码（400/404/409/502/500）。</summary>
public sealed class WebApiHostException(WebApiHostErrorKind kind, string message) : Exception(message)
{
    public WebApiHostErrorKind Kind { get; } = kind;
}

/// <summary>把 WebApiHostException 映射为 HTTP 状态码（对齐 webapi.hostErrorStatus）。</summary>
public static class WebApiHostErrorMapper
{
    public static int StatusCode(WebApiHostErrorKind kind) => kind switch
    {
        WebApiHostErrorKind.Invalid => 400,
        WebApiHostErrorKind.NotFound => 404,
        WebApiHostErrorKind.Conflict => 409,
        WebApiHostErrorKind.Connection => 502,
        _ => 500,
    };
}

// ── 主机 DTO（store/models.go HostView 与 hostmanager.Input 的 json tag）──
public sealed record WebApiHostInput
{
    public string Name { get; init; } = "";
    public string Addr { get; init; } = "";
    public int Port { get; init; }
    public string Username { get; init; } = "";
    public string AuthType { get; init; } = "";
    /// <summary>仅在创建/更新请求中出现，不回显。</summary>
    public string Password { get; init; } = "";
    public long? KeyId { get; init; }
    public long? JumpHostId { get; init; }
    public bool MonitorEnabled { get; init; }
    public string[] Tags { get; init; } = [];
}

public sealed record WebApiHostView
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Addr { get; init; } = "";
    public int Port { get; init; }
    public string Username { get; init; } = "";
    public string AuthType { get; init; } = "";
    public long? KeyId { get; init; }
    public string? HostKeyFp { get; init; }
    public long? JumpHostId { get; init; }
    public bool MonitorEnabled { get; init; }
    public string[] Tags { get; init; } = [];
    public long CreatedAt { get; init; }
    public bool Archived { get; init; }
    public long? ArchivedAt { get; init; }
}

/// <summary>主机连通性测试结果（hostmanager.Test 返回结构）。</summary>
public sealed record WebApiHostTestResult(bool Ok, string? Error);

// ── 密钥 DTO（store.Key；PrivateKeyEnc 带 json:"-" 不出网）──
public sealed record WebApiKeyView(
    long Id,
    string Name,
    string PublicKey,
    long CreatedAt);

// ── 令牌 DTO（store.Token + webapi.tokenView）──
public sealed record WebApiToken
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public bool AllHosts { get; init; }
    public bool ManageHosts { get; init; }
    public string[] DisabledTools { get; init; } = [];
    public string Source { get; init; } = "";
    public long CreatedAt { get; init; }
}

public sealed record WebApiTokenCreate
{
    public string Name { get; init; } = "";
    public string Hash { get; init; } = "";
    public bool AllHosts { get; init; }
    public bool ManageHosts { get; init; }
    public string[] DisabledTools { get; init; } = [];
    public long[] HostIds { get; init; } = [];
}

public sealed record WebApiTokenUpdate
{
    public string Name { get; init; } = "";
    public bool AllHosts { get; init; }
    public bool ManageHosts { get; init; }
    public long[] HostIds { get; init; } = [];
    public string[] DisabledTools { get; init; } = [];
}

/// <summary>令牌视图：Token 字段 + host_ids（可选）+ token 明文（仅创建响应，omitempty）。</summary>
public sealed record WebApiTokenView
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public bool AllHosts { get; init; }
    public bool ManageHosts { get; init; }
    public string[] DisabledTools { get; init; } = [];
    public string Source { get; init; } = "";
    public long CreatedAt { get; init; }
    public long[]? HostIds { get; init; }
    public string? Token { get; init; }
}

// ── 任务 DTO（store.Job）──
public sealed record WebApiJob
{
    public string Id { get; init; } = "";
    public long HostId { get; init; }
    public string Command { get; init; } = "";
    public string Cwd { get; init; } = "";
    public long? Pid { get; init; }
    public bool UsedSetsid { get; init; }
    public string Status { get; init; } = "";
    public long? ExitCode { get; init; }
    public long LogBytes { get; init; }
    public long StartedAt { get; init; }
    public long? FinishedAt { get; init; }
}

// ── 审计视图（store.Audit 的 Go 默认 JSON 键名：ID/Ts/TokenID/.../ParamsJSON/OK/DurationMS/BytesOut；
//    Go 的 sql.Null* 包装在这里简化为「值或 null」——已列入移植差异报告）──
public sealed record WebApiAuditView
{
    public long Id { get; init; }
    public long Ts { get; init; }
    public long? TokenId { get; init; }
    public string? TokenName { get; init; }
    public string Tool { get; init; } = "";
    public string? Host { get; init; }
    public string ParamsJson { get; init; } = "";
    public string[]? RunIds { get; init; }
    public bool Ok { get; init; }
    public long? ExitCode { get; init; }
    public long DurationMs { get; init; }
    public long BytesOut { get; init; }
}

// ── 命令执行记录（command_runs.go commandRunView）──
public sealed record WebApiCommandRun
{
    public long Seq { get; init; }
    public string Id { get; init; } = "";
    public long? TokenId { get; init; }
    public string? TokenName { get; init; }
    public string Tool { get; init; } = "";
    public long? HostId { get; init; }
    public string Host { get; init; } = "";
    public string Command { get; init; } = "";
    public string Cwd { get; init; } = "";
    public string? Session { get; init; }
    public string? JobId { get; init; }
    public string Status { get; init; } = "";
    public long? ExitCode { get; init; }
    public long StdoutBytes { get; init; }
    public long StderrBytes { get; init; }
    public bool OutputAvailable { get; init; }
    public bool OutputExpired { get; init; }
    public string? OutputError { get; init; }
    public string? Error { get; init; }
    public long StartedAt { get; init; }
    public long? FinishedAt { get; init; }
    public string? StdoutPreview { get; init; }
    public string? StderrPreview { get; init; }
    public long DurationMs { get; init; }
}

public sealed record WebApiCommandRunFilter
{
    public long[] TokenIds { get; init; } = [];
    public string[] Hosts { get; init; } = [];
    public string[] Tools { get; init; } = [];
    public string[] Statuses { get; init; } = [];
    public string Query { get; init; } = "";
    public long Before { get; init; }
    public int Limit { get; init; }
}

/// <summary>输出分块（execx/jobs LogChunk 返回：{"content","complete",...}）。</summary>
public sealed record WebApiOutputChunk(
    string Content,
    bool Complete,
    long? TotalBytes = null,
    string? Error = null);

// ── 监控（monitor.FromMetric 近似：ts/cpu/mem/load/disks；已列入差异报告）──
public sealed record WebApiMetricSnapshot
{
    public long Ts { get; init; }
    public double? CpuPct { get; init; }
    public long? MemUsedKb { get; init; }
    public long? MemTotalKb { get; init; }
    public double? Load1 { get; init; }
    public string DisksJson { get; init; } = "";
}

public sealed record WebApiMetric(long HostId, long Ts, double? CpuPct, long? MemUsedKb, long? MemTotalKb, double? Load1, string DisksJson);

// ── 记忆 DTO（memories.go + store.MemoryAdminRow / MemoryBankStat）──
public sealed record WebApiMemoryAdminRow
{
    public long Id { get; init; }
    public long? HostId { get; init; }
    public string? HostName { get; init; }
    public string Content { get; init; } = "";
    public string Source { get; init; } = "";
    public double Importance { get; init; }
    public string Veracity { get; init; } = "";
    public long CreatedAt { get; init; }
    public long UpdatedAt { get; init; }
    public long RecallCount { get; init; }
}

public sealed record WebApiMemoryBankStatRaw(long? HostId, long Count, long Embedded, long? LastWritten);

/// <summary>memoryAdminBankStat（GET /memories/stats 响应元素）。</summary>
public sealed record WebApiMemoryBankStat(
    long? HostId,
    string? HostName,
    long Count,
    long Embedded,
    long? LastWritten);

// ── 文件 DTO（files.List / files.Write 返回；近似字段，已列入差异报告）──
public sealed record WebApiFileItem(
    string Name,
    string Path,
    bool IsDir,
    long Size,
    long Mtime,
    string? Mode = null);

public sealed record WebApiFileWriteResult(
    string Path,
    long Size,
    string Sha256);

/// <summary>SFTP 读取句柄：内容流 + 大小（对齐 files.OpenRead(file, size)）。</summary>
public sealed record WebApiFileReadStream(Stream Content, long Size) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        if (Content is not null) await Content.DisposeAsync();
    }
}

// ── 事件（events.Event：SSE data 载荷 {"type","ts","data"}）──
public sealed record WebApiEvent(
    string Type,
    long Ts,
    object? Data);

/// <summary>SSE 订阅句柄（TryRead 拉取下一条，无消息返回 null；Dispose 退订）。</summary>
public sealed class WebApiEventSubscription : IDisposable
{
    public required Func<WebApiEvent?> TryRead { get; init; }
    private readonly Action _dispose;
    public WebApiEventSubscription(Action dispose) => _dispose = dispose;
    public void Dispose() => _dispose();
}

// ── 服务接口 ──

/// <summary>WebApi 所需的存储方法集（对应 store.Store 的子集，由 Plugin.Db 或等价插件提供）。</summary>
public interface IWebApiStore
{
    Task<IReadOnlyList<WebApiHostView>> ListHostsAsync(CancellationToken ct = default);
    Task<WebApiHostView> GetHostAsync(long id, CancellationToken ct = default);
    Task<WebApiHostView> GetHostByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<WebApiKeyView>> ListKeysAsync(CancellationToken ct = default);
    Task<WebApiKeyView> CreateKeyAsync(string name, byte[] privateKeyEnc, string publicKey, CancellationToken ct = default);
    Task DeleteKeyAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<WebApiToken>> ListTokensAsync(CancellationToken ct = default);
    Task<long[]> TokenHostIdsAsync(long tokenId, CancellationToken ct = default);
    Task<WebApiToken> CreateTokenAsync(WebApiTokenCreate create, CancellationToken ct = default);
    /// <summary>令牌不存在时抛 WebApiHostException(NotFound)。</summary>
    Task<WebApiToken> UpdateTokenAsync(long id, WebApiTokenUpdate update, CancellationToken ct = default);
    Task DeleteTokenAsync(long id, CancellationToken ct = default);
    /// <summary>校验 host_ids 全部存在；失败抛 WebApiHostException(Invalid)。</summary>
    Task<long[]> ValidateHostIdsAsync(long[] hostIds, CancellationToken ct = default);
    Task<IReadOnlyList<WebApiJob>> ListJobsAsync(long? hostId, CancellationToken ct = default);
    /// <summary>任务不存在时抛 WebApiHostException(NotFound)。</summary>
    Task<WebApiJob> GetJobAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<WebApiAuditView>> ListAuditAsync(long[] tokenIds, string[] hosts, string[] tools, bool? ok, long before, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListAuditToolsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<WebApiMetric>> MetricsSinceAsync(long hostId, long sinceTsMs, CancellationToken ct = default);
    Task<IReadOnlyList<WebApiMemoryAdminRow>> ListMemoriesAdminAsync(long? hostId, string query, int limit, int offset, CancellationToken ct = default);
    /// <summary>记忆不存在时抛 WebApiHostException(NotFound)。</summary>
    Task DeleteMemoryAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<WebApiMemoryBankStatRaw>> MemoryStatsAsync(CancellationToken ct = default);
    /// <summary>命令记录不存在时抛 WebApiHostException(NotFound)。</summary>
    Task<WebApiCommandRun> GetCommandRunAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<WebApiCommandRun>> ListCommandRunsAsync(WebApiCommandRunFilter filter, CancellationToken ct = default);
}

/// <summary>主机管理服务（对应 hostmanager.Manager，由 Plugin.HostManager 提供）。</summary>
public interface IWebApiHostManager
{
    Task<WebApiHostView> CreateAsync(WebApiHostInput input, CancellationToken ct = default);
    Task<WebApiHostView> UpdateAsync(long id, WebApiHostInput input, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task<WebApiHostTestResult> TestAsync(long id, CancellationToken ct = default);
    Task ResetFingerprintAsync(long id, CancellationToken ct = default);
    Task<WebApiHostView> ArchiveAsync(long id, CancellationToken ct = default);
    Task<WebApiHostView> UnarchiveAsync(long id, CancellationToken ct = default);
}

/// <summary>
/// SSH 连接池（对应 sshpool.Pool；由 Plugin.SshPool 提供）。
/// TODO(port)：Plugin.SshPool 未就绪，此接口为占位；terminal WS 在池缺失时返回 502。
/// </summary>
public interface IWebApiConnectionPool
{
    /// <summary>获取主机交互式 shell 会话（PTY）。TODO(port)：对齐 sshpool.Get + client.NewSession。</summary>
    Task<IWebApiSshShell?> OpenShellAsync(string host, int rows, int cols, CancellationToken ct = default);
}

/// <summary>交互式 SSH shell 会话占位（TODO(port)：对齐 golang.org/x/crypto/ssh Session）。</summary>
public interface IWebApiSshShell : IAsyncDisposable
{
    Task WriteInputAsync(string data, CancellationToken ct = default);
    Task ResizeAsync(int rows, int cols, CancellationToken ct = default);
    /// <summary>读取一段输出；返回 null 表示流结束。</summary>
    Task<byte[]?> ReadOutputAsync(CancellationToken ct = default);
}

/// <summary>命令输出读取（对应 execx.Runner.ReadCommandOutput，由 Plugin.ExecRunner 提供）。</summary>
public interface IWebApiCommandRunner
{
    Task<WebApiOutputChunk> ReadCommandOutputAsync(string runId, string stream, long offsetBytes, int limitBytes, CancellationToken ct = default);
}

/// <summary>后台任务管理（对应 jobs.Manager，由 Plugin.Jobs 提供）。</summary>
public interface IWebApiJobManager
{
    /// <summary>刷新全部任务状态（jobs.RefreshAll）。</summary>
    Task<IReadOnlyList<WebApiJob>> RefreshAllAsync(IReadOnlyList<WebApiJob> jobs, CancellationToken ct = default);
    Task<WebApiJob> RefreshAsync(WebApiJob job, CancellationToken ct = default);
    Task KillAsync(WebApiJob job, string signal, CancellationToken ct = default);
    Task<WebApiOutputChunk> LogChunkAsync(WebApiJob job, long offsetBytes, int limitBytes, CancellationToken ct = default);
}

/// <summary>文件管理（对应 files.Manager，由 Plugin.Files 提供）。</summary>
public interface IWebApiFileManager
{
    Task<IReadOnlyList<WebApiFileItem>> ListAsync(string host, string path, CancellationToken ct = default);
    Task<WebApiFileReadStream> OpenReadAsync(string host, string path, long maxSize, CancellationToken ct = default);
    Task<WebApiFileWriteResult> WriteAsync(string host, string path, byte[] data, string mode, CancellationToken ct = default);
}

/// <summary>SSE 事件总线（包装 SubSsh.Core.Events.EventBus，语义对齐 events.Bus）。</summary>
public interface IWebApiEventBus
{
    void Publish(string type, object? data);
    WebApiEventSubscription Subscribe();
}
