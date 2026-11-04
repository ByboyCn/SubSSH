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
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("addr")] public string Addr { get; init; } = "";
    [JsonPropertyName("port")] public int Port { get; init; }
    [JsonPropertyName("username")] public string Username { get; init; } = "";
    [JsonPropertyName("auth_type")] public string AuthType { get; init; } = "";
    /// <summary>仅在创建/更新请求中出现，不回显。</summary>
    [JsonPropertyName("password")] public string Password { get; init; } = "";
    [JsonPropertyName("key_id")] public long? KeyId { get; init; }
    [JsonPropertyName("jump_host_id")] public long? JumpHostId { get; init; }
    [JsonPropertyName("monitor_enabled")] public bool MonitorEnabled { get; init; }
    [JsonPropertyName("tags")] public string[] Tags { get; init; } = [];
}

public sealed record WebApiHostView
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("addr")] public string Addr { get; init; } = "";
    [JsonPropertyName("port")] public int Port { get; init; }
    [JsonPropertyName("username")] public string Username { get; init; } = "";
    [JsonPropertyName("auth_type")] public string AuthType { get; init; } = "";
    [JsonPropertyName("key_id")] public long? KeyId { get; init; }
    [JsonPropertyName("hostkey_fp")] public string? HostKeyFp { get; init; }
    [JsonPropertyName("jump_host_id")] public long? JumpHostId { get; init; }
    [JsonPropertyName("monitor_enabled")] public bool MonitorEnabled { get; init; }
    [JsonPropertyName("tags")] public string[] Tags { get; init; } = [];
    [JsonPropertyName("created_at")] public long CreatedAt { get; init; }
    [JsonPropertyName("archived")] public bool Archived { get; init; }
    [JsonPropertyName("archived_at")] public long? ArchivedAt { get; init; }
}

/// <summary>主机连通性测试结果（hostmanager.Test 返回结构）。</summary>
public sealed record WebApiHostTestResult([property: JsonPropertyName("ok")] bool Ok, [property: JsonPropertyName("error")] string? Error);

// ── 密钥 DTO（store.Key；PrivateKeyEnc 带 json:"-" 不出网）──
public sealed record WebApiKeyView(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("public_key")] string PublicKey,
    [property: JsonPropertyName("created_at")] long CreatedAt);

// ── 令牌 DTO（store.Token + webapi.tokenView）──
public sealed record WebApiToken
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("all_hosts")] public bool AllHosts { get; init; }
    [JsonPropertyName("manage_hosts")] public bool ManageHosts { get; init; }
    [JsonPropertyName("disabled_tools")] public string[] DisabledTools { get; init; } = [];
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("created_at")] public long CreatedAt { get; init; }
}

public sealed record WebApiTokenCreate
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("hash")] public string Hash { get; init; } = "";
    [JsonPropertyName("all_hosts")] public bool AllHosts { get; init; }
    [JsonPropertyName("manage_hosts")] public bool ManageHosts { get; init; }
    [JsonPropertyName("disabled_tools")] public string[] DisabledTools { get; init; } = [];
    [JsonPropertyName("host_ids")] public long[] HostIds { get; init; } = [];
}

public sealed record WebApiTokenUpdate
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("all_hosts")] public bool AllHosts { get; init; }
    [JsonPropertyName("manage_hosts")] public bool ManageHosts { get; init; }
    [JsonPropertyName("host_ids")] public long[] HostIds { get; init; } = [];
    [JsonPropertyName("disabled_tools")] public string[] DisabledTools { get; init; } = [];
}

/// <summary>令牌视图：Token 字段 + host_ids（可选）+ token 明文（仅创建响应，omitempty）。</summary>
public sealed record WebApiTokenView
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("all_hosts")] public bool AllHosts { get; init; }
    [JsonPropertyName("manage_hosts")] public bool ManageHosts { get; init; }
    [JsonPropertyName("disabled_tools")] public string[] DisabledTools { get; init; } = [];
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("created_at")] public long CreatedAt { get; init; }
    [JsonPropertyName("host_ids")] public long[]? HostIds { get; init; }
    [JsonPropertyName("token")] public string? Token { get; init; }
}

// ── 任务 DTO（store.Job）──
public sealed record WebApiJob
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("host_id")] public long HostId { get; init; }
    [JsonPropertyName("command")] public string Command { get; init; } = "";
    [JsonPropertyName("cwd")] public string Cwd { get; init; } = "";
    [JsonPropertyName("pid")] public long? Pid { get; init; }
    [JsonPropertyName("used_setsid")] public bool UsedSetsid { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("exit_code")] public long? ExitCode { get; init; }
    [JsonPropertyName("log_bytes")] public long LogBytes { get; init; }
    [JsonPropertyName("started_at")] public long StartedAt { get; init; }
    [JsonPropertyName("finished_at")] public long? FinishedAt { get; init; }
}

// ── 审计视图（store.Audit 的 Go 默认 JSON 键名：ID/Ts/TokenID/.../ParamsJSON/OK/DurationMS/BytesOut；
//    Go 的 sql.Null* 包装在这里简化为「值或 null」——已列入移植差异报告）──
public sealed record WebApiAuditView
{
    [JsonPropertyName("ID")] public long Id { get; init; }
    [JsonPropertyName("Ts")] public long Ts { get; init; }
    [JsonPropertyName("TokenID")] public long? TokenId { get; init; }
    [JsonPropertyName("TokenName")] public string? TokenName { get; init; }
    [JsonPropertyName("Tool")] public string Tool { get; init; } = "";
    [JsonPropertyName("Host")] public string? Host { get; init; }
    [JsonPropertyName("ParamsJSON")] public string ParamsJson { get; init; } = "";
    [JsonPropertyName("RunIDs")] public string[]? RunIds { get; init; }
    [JsonPropertyName("OK")] public bool Ok { get; init; }
    [JsonPropertyName("ExitCode")] public long? ExitCode { get; init; }
    [JsonPropertyName("DurationMS")] public long DurationMs { get; init; }
    [JsonPropertyName("BytesOut")] public long BytesOut { get; init; }
}

// ── 命令执行记录（command_runs.go commandRunView）──
public sealed record WebApiCommandRun
{
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("token_id")] public long? TokenId { get; init; }
    [JsonPropertyName("token_name")] public string? TokenName { get; init; }
    [JsonPropertyName("tool")] public string Tool { get; init; } = "";
    [JsonPropertyName("host_id")] public long? HostId { get; init; }
    [JsonPropertyName("host")] public string Host { get; init; } = "";
    [JsonPropertyName("command")] public string Command { get; init; } = "";
    [JsonPropertyName("cwd")] public string Cwd { get; init; } = "";
    [JsonPropertyName("session")] public string? Session { get; init; }
    [JsonPropertyName("job_id")] public string? JobId { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("exit_code")] public long? ExitCode { get; init; }
    [JsonPropertyName("stdout_bytes")] public long StdoutBytes { get; init; }
    [JsonPropertyName("stderr_bytes")] public long StderrBytes { get; init; }
    [JsonPropertyName("output_available")] public bool OutputAvailable { get; init; }
    [JsonPropertyName("output_expired")] public bool OutputExpired { get; init; }
    [JsonPropertyName("output_error")] public string? OutputError { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("started_at")] public long StartedAt { get; init; }
    [JsonPropertyName("finished_at")] public long? FinishedAt { get; init; }
    [JsonPropertyName("stdout_preview")] public string? StdoutPreview { get; init; }
    [JsonPropertyName("stderr_preview")] public string? StderrPreview { get; init; }
    [JsonPropertyName("duration_ms")] public long DurationMs { get; init; }
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
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("total_bytes")] long? TotalBytes = null,
    [property: JsonPropertyName("error")] string? Error = null);

// ── 监控（monitor.FromMetric 近似：ts/cpu/mem/load/disks；已列入差异报告）──
public sealed record WebApiMetricSnapshot
{
    [JsonPropertyName("ts")] public long Ts { get; init; }
    [JsonPropertyName("cpu_pct")] public double? CpuPct { get; init; }
    [JsonPropertyName("mem_used_kb")] public long? MemUsedKb { get; init; }
    [JsonPropertyName("mem_total_kb")] public long? MemTotalKb { get; init; }
    [JsonPropertyName("load1")] public double? Load1 { get; init; }
    [JsonPropertyName("disks")] public string DisksJson { get; init; } = "";
}

public sealed record WebApiMetric(long HostId, long Ts, double? CpuPct, long? MemUsedKb, long? MemTotalKb, double? Load1, string DisksJson);

// ── 记忆 DTO（memories.go + store.MemoryAdminRow / MemoryBankStat）──
public sealed record WebApiMemoryAdminRow
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("host_id")] public long? HostId { get; init; }
    [JsonPropertyName("host_name")] public string? HostName { get; init; }
    [JsonPropertyName("content")] public string Content { get; init; } = "";
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("importance")] public double Importance { get; init; }
    [JsonPropertyName("veracity")] public string Veracity { get; init; } = "";
    [JsonPropertyName("created_at")] public long CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public long UpdatedAt { get; init; }
    [JsonPropertyName("recall_count")] public long RecallCount { get; init; }
}

public sealed record WebApiMemoryBankStatRaw(long? HostId, long Count, long Embedded, long? LastWritten);

/// <summary>memoryAdminBankStat（GET /memories/stats 响应元素）。</summary>
public sealed record WebApiMemoryBankStat(
    [property: JsonPropertyName("host_id")] long? HostId,
    [property: JsonPropertyName("host_name")] string? HostName,
    [property: JsonPropertyName("count")] long Count,
    [property: JsonPropertyName("embedded")] long Embedded,
    [property: JsonPropertyName("last_written")] long? LastWritten);

// ── 文件 DTO（files.List / files.Write 返回；近似字段，已列入差异报告）──
public sealed record WebApiFileItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("is_dir")] bool IsDir,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("mtime")] long Mtime,
    [property: JsonPropertyName("mode")] string? Mode = null);

public sealed record WebApiFileWriteResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

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
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("ts")] long Ts,
    [property: JsonPropertyName("data")] object? Data);

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
