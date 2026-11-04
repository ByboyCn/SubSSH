// SubSsh.Core.Abstractions — Store 公共组件接口扩展 + 数据模型（字段对齐 Go 版 internal/store/models.go）。
// 说明：ServiceAbstractions.cs 中的 IOneSshStore 是骨架子集（HealthAsync/ConnectionString）。
// 为避免改动 ServiceAbstractions.cs（可能与并行任务冲突），此处定义完整方法集接口 IOneSshStoreFull : IOneSshStore；
// 消费方 Require<IOneSshStoreFull>() 即可拿到全量存储能力（同一实例，Db 插件同时 Provide 两个接口）。
namespace SubSsh.Core.Abstractions;

// ---------- 数据模型（命名空间仍为 Core.Abstractions；字段/JSON 名对齐 store/models.go） ----------

/// <summary>SSH 密钥（Go Key）。PrivateKeyEnc 为 ICryptoBox 加密后的密文，store 层只存不解释。</summary>
public sealed class SshKey
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public byte[] PrivateKeyEnc { get; set; } = [];
    public string PublicKey { get; set; } = "";
    public long CreatedAt { get; set; }
}

/// <summary>主机配置（Go Host）。可空字段对齐 Go sql.Null*；Tags 永不为 null。</summary>
public sealed class HostConfig
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Addr { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string AuthType { get; set; } = ""; // 'key' | 'password'
    public long? KeyId { get; set; }
    public byte[]? PasswordEnc { get; set; }
    public string? HostKeyFp { get; set; }
    public long? JumpHostId { get; set; }
    public bool MonitorEnabled { get; set; }
    public string[] Tags { get; set; } = [];
    public long CreatedAt { get; set; }
    /// <summary>非空表示已归档：保留配置与历史，但不监控、不对 MCP 令牌可见。</summary>
    public long? ArchivedAt { get; set; }

    /// <summary>Go Host.View()：脱敏视图（json 字段名与原版一致）。</summary>
    public HostViewConfig View() => new()
    {
        Id = Id,
        Name = Name,
        Addr = Addr,
        Port = Port,
        Username = Username,
        AuthType = AuthType,
        KeyId = KeyId,
        HostKeyFp = HostKeyFp,
        JumpHostId = JumpHostId,
        MonitorEnabled = MonitorEnabled,
        Tags = Tags,
        CreatedAt = CreatedAt,
        Archived = ArchivedAt != null,
        ArchivedAt = ArchivedAt,
    };
}

/// <summary>主机配置视图（Go HostView；json tag 与原版一致）。</summary>
public sealed class HostViewConfig
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Addr { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string AuthType { get; set; } = "";
    public long? KeyId { get; set; }
    public string? HostKeyFp { get; set; }
    public long? JumpHostId { get; set; }
    public bool MonitorEnabled { get; set; }
    public string[] Tags { get; set; } = [];
    public long CreatedAt { get; set; }
    public bool Archived { get; set; }
    public long? ArchivedAt { get; set; }
}

/// <summary>API 令牌（Go Token；json tag 与原版一致）。</summary>
public sealed class ApiToken
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool AllHosts { get; set; }
    public bool ManageHosts { get; set; }
    public string[] DisabledTools { get; set; } = [];
    public string Source { get; set; } = "";
    public long CreatedAt { get; set; }
    public long? LastUsedAt { get; set; }
    public long? ExpiresAt { get; set; }
    public string? Resource { get; set; }
    public string? ClientId { get; set; }
}

/// <summary>TokenCreate（Go store.TokenCreate）。</summary>
public sealed class TokenCreate
{
    public string Name { get; set; } = "";
    public string Hash { get; set; } = "";
    public bool AllHosts { get; set; }
    public bool ManageHosts { get; set; }
    public string[] DisabledTools { get; set; } = [];
    public long[] HostIds { get; set; } = [];
    public string Source { get; set; } = "";
    public long ExpiresAt { get; set; }
    public string Resource { get; set; } = "";
    public string ClientId { get; set; } = "";
}

/// <summary>TokenUpdate（Go store.TokenUpdate）：手动更新主机范围与工具组 denylist。</summary>
public sealed class TokenUpdate
{
    public string Name { get; set; } = "";
    public bool AllHosts { get; set; }
    public bool ManageHosts { get; set; }
    public long[] HostIds { get; set; } = [];
    public string[] DisabledTools { get; set; } = [];
}

/// <summary>session（execx cwd/env 持久化，Go Session）。</summary>
public sealed class SessionRecord
{
    public long Id { get; set; }
    public long TokenId { get; set; }
    public long HostId { get; set; }
    public string Label { get; set; } = "";
    public string Cwd { get; set; } = "";
    public Dictionary<string, string> Env { get; set; } = [];
    public long UpdatedAt { get; set; }
}

/// <summary>后台任务（Go Job；json tag 与原版一致）。</summary>
public sealed class JobRecord
{
    public string Id { get; set; } = "";
    public long HostId { get; set; }
    public long? TokenId { get; set; }
    public string Command { get; set; } = "";
    public string Cwd { get; set; } = "";
    public long? Pid { get; set; }
    public bool UsedSetsid { get; set; }
    public string Status { get; set; } = "";
    public long? ExitCode { get; set; }
    public long LogBytes { get; set; }
    public long StartedAt { get; set; }
    public long? FinishedAt { get; set; }
}

/// <summary>审计条目（Go Audit；RunIDs JSON null 时保持 null，序列化 omitempty 语义由输出层负责）。</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public long Ts { get; set; }
    public long? TokenId { get; set; }
    public string? TokenName { get; set; }
    public string Tool { get; set; } = "";
    public string? Host { get; set; }
    public string ParamsJson { get; set; } = "";
    public string[]? RunIds { get; set; }
    public bool Ok { get; set; }
    public long? ExitCode { get; set; }
    public long DurationMs { get; set; }
    public long BytesOut { get; set; }
}

/// <summary>用户可见的远程命令执行记录（Go CommandRun）。</summary>
public sealed class CommandRun
{
    public long Seq { get; set; }
    public string Id { get; set; } = "";
    public long? TokenId { get; set; }
    public string? TokenName { get; set; }
    public string Tool { get; set; } = "";
    public long? HostId { get; set; }
    public string Host { get; set; } = "";
    public string Command { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string? Session { get; set; }
    public string? JobId { get; set; }
    public string Status { get; set; } = "";
    public long? ExitCode { get; set; }
    public string StdoutPreview { get; set; } = "";
    public string StderrPreview { get; set; } = "";
    public long StdoutBytes { get; set; }
    public long StderrBytes { get; set; }
    public bool OutputAvailable { get; set; }
    public bool OutputExpired { get; set; }
    public string? OutputError { get; set; }
    public string? ErrorText { get; set; }
    public long StartedAt { get; set; }
    public long? FinishedAt { get; set; }
}

/// <summary>命令记录完成快照（Go CommandRunFinish）。</summary>
public sealed class CommandRunFinish
{
    public string Status { get; set; } = "";
    public long? ExitCode { get; set; }
    public string StdoutPreview { get; set; } = "";
    public string StderrPreview { get; set; } = "";
    public long StdoutBytes { get; set; }
    public long StderrBytes { get; set; }
    public bool OutputAvailable { get; set; }
    public string? OutputError { get; set; }
    public string? ErrorText { get; set; }
    public long FinishedAt { get; set; }
}

/// <summary>命令记录筛选（Go CommandRunFilter）。</summary>
public sealed class CommandRunFilter
{
    public long[] TokenIds { get; set; } = [];
    public string[] Hosts { get; set; } = [];
    public string[] Tools { get; set; } = [];
    public string[] Statuses { get; set; } = [];
    public string Query { get; set; } = "";
    public long Before { get; set; }
    public int Limit { get; set; }
}

/// <summary>主机监控采样（Go Metric）。</summary>
public sealed class MetricRecord
{
    public long HostId { get; set; }
    public long Ts { get; set; }
    public double? CpuPct { get; set; }
    public long? MemUsedKb { get; set; }
    public long? MemTotalKb { get; set; }
    public double? Load1 { get; set; }
    public string DisksJson { get; set; } = "";
}

/// <summary>记忆条目（Go Memory；json tag 与原版一致）。</summary>
public sealed class MemoryRecord
{
    public long Id { get; set; }
    public long? HostId { get; set; }
    public string Content { get; set; } = "";
    public string Source { get; set; } = "";
    public double Importance { get; set; }
    public string Veracity { get; set; } = "";
    public long? TokenId { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public long RecallCount { get; set; }
    public long? LastRecalled { get; set; }
    public byte[]? Embedding { get; set; }
    public string? EmbeddingModel { get; set; }
}

/// <summary>带相关性分数的记忆（Go MemoryWithRank）。</summary>
public sealed class MemoryWithRank
{
    public MemoryRecord Memory { get; set; } = new();
    public double Rank { get; set; }
}

/// <summary>嵌入向量（Go MemoryVector）。</summary>
public sealed class MemoryVector
{
    public long Id { get; set; }
    public byte[]? Embedding { get; set; }
}

/// <summary>记忆管理行（Go MemoryAdminRow；json tag 与原版一致）。</summary>
public sealed class MemoryAdminRow
{
    public long Id { get; set; }
    public long? HostId { get; set; }
    public string? HostName { get; set; }
    public string Content { get; set; } = "";
    public string Source { get; set; } = "";
    public double Importance { get; set; }
    public string Veracity { get; set; } = "";
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public long RecallCount { get; set; }
    public long? LastRecalled { get; set; }
}

/// <summary>记忆库统计（Go MemoryBankStat；json tag 与原版一致）。</summary>
public sealed class MemoryBankStat
{
    public long? HostId { get; set; }
    public long Count { get; set; }
    public long Embedded { get; set; }
    public long? LastWritten { get; set; }
}

// ---------- OAuth 模型（Go store oauth.go 顶层类型） ----------
// OAuthAuthorizationCode / OAuthAuthorizationCodeExchange / OAuthRefreshTokenRotation
// 定义在 OAuthAbstractions.cs（record 版），此处不再重复定义。

public sealed class OAuthClientRecord
{
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string ClientUri { get; set; } = "";
    public string[] RedirectUris { get; set; } = [];
    public long CreatedAt { get; set; }
}

public sealed class OAuthRefreshTokenRecord
{
    public string TokenHash { get; set; } = "";
    public string GrantId { get; set; } = "";
    public long AccessTokenId { get; set; }
    public string ClientId { get; set; } = "";
    public string Resource { get; set; } = "";
    public string Scope { get; set; } = "";
    public bool AllHosts { get; set; }
    public bool ManageHosts { get; set; }
    public string[] DisabledTools { get; set; } = [];
    public long[] HostIds { get; set; } = [];
    public long ExpiresAt { get; set; }
    public long CreatedAt { get; set; }
    public long? UsedAt { get; set; }
    public long? RevokedAt { get; set; }
}

// ---------- 完整方法集接口 ----------

/// <summary>
/// 数据库公共组件完整方法集（对应 Go internal/store 全部方法）。
/// 失败语义对齐 Go 版：查询无行返回 null；业务约束（如密钥被引用、主机被用作跳板、OAuth 令牌重用）
/// 抛 SubSshException，文案与 Go 版一致。
/// </summary>
public interface IOneSshStoreFull : IOneSshStore
{
    // ---- 维护（store.go）----
    /// <summary>WAL checkpoint（TRUNCATE）。busy=true 表示被其他连接阻塞。</summary>
    Task<(bool Busy, long Log, long Checkpointed)> CheckpointWalAsync(CancellationToken ct = default);

    // ---- keys & hosts（hosts_keys.go）----
    Task<SshKey> CreateKeyAsync(string name, byte[] enc, string publicKey, CancellationToken ct = default);
    Task<IReadOnlyList<SshKey>> ListKeysAsync(CancellationToken ct = default);
    Task<SshKey?> GetKeyAsync(long id, CancellationToken ct = default);
    /// <summary>删除密钥；仍被主机引用时抛 SubSshException("密钥正被 N 个主机引用")。</summary>
    Task DeleteKeyAsync(long id, CancellationToken ct = default);
    Task<HostConfig> CreateHostAsync(HostConfig host, CancellationToken ct = default);
    Task UpdateHostAsync(HostConfig host, CancellationToken ct = default);
    /// <summary>删除主机；运行中任务/跳板依赖时抛对应 SubSshException；不存在时返回 false。</summary>
    Task<bool> DeleteHostAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<HostConfig>> ListHostsAsync(CancellationToken ct = default);
    Task<HostConfig?> GetHostByNameAsync(string name, CancellationToken ct = default);
    Task<HostConfig?> GetHostAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<string>> JumpDependentNamesAsync(long id, CancellationToken ct = default);
    Task UpdateHostFingerprintAsync(long id, string? fingerprint, CancellationToken ct = default);
    /// <summary>归档主机；仍被未归档主机用作跳板时抛 SubSshException。</summary>
    Task ArchiveHostAsync(long id, long at, CancellationToken ct = default);
    /// <summary>取消归档；跳板主机仍归档时抛 SubSshException；运行中任务重新起算失联宽限期。</summary>
    Task UnarchiveHostAsync(long id, long now, CancellationToken ct = default);
    /// <summary>未归档且开启监控的主机（归档主机即使开着监控开关也不再轮询）。</summary>
    Task<IReadOnlyList<HostConfig>> MonitoredHostsAsync(CancellationToken ct = default);

    // ---- tokens & sessions（auth_sessions.go）----
    Task<ApiToken> CreateTokenAsync(TokenCreate input, CancellationToken ct = default);
    /// <summary>按哈希查令牌（不带 resource 校验）；令牌不存在或已过期返回 (null, [])。</summary>
    Task<(ApiToken? Token, IReadOnlyList<HostConfig> Hosts)> FindTokenAsync(string hash, CancellationToken ct = default);
    /// <summary>按哈希查令牌并要求 resource 匹配（manual 令牌豁免）。</summary>
    Task<(ApiToken? Token, IReadOnlyList<HostConfig> Hosts)> FindTokenForResourceAsync(string hash, string resource, CancellationToken ct = default);
    Task<IReadOnlyList<ApiToken>> ListTokensAsync(CancellationToken ct = default);
    /// <summary>手动更新令牌；不存在时抛 SubSshException。</summary>
    Task<ApiToken> UpdateTokenAsync(long id, TokenUpdate input, CancellationToken ct = default);
    Task<ApiToken?> GetTokenAsync(long id, CancellationToken ct = default);
    Task DeleteTokenAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<long>> TokenHostIdsAsync(long id, CancellationToken ct = default);
    /// <summary>读 session；不存在时返回默认值（Cwd="~"、空 env），与 Go 版一致。</summary>
    Task<SessionRecord> GetSessionAsync(long tokenId, long hostId, string label, CancellationToken ct = default);
    Task SaveSessionAsync(SessionRecord session, CancellationToken ct = default);

    // ---- command runs（command_runs.go）----
    Task CreateCommandRunAsync(CommandRun run, CancellationToken ct = default);
    /// <summary>同步命令完成；记录不存在或已结束时抛 SubSshException。</summary>
    Task FinishCommandRunAsync(string id, CommandRunFinish finish, CancellationToken ct = default);
    /// <summary>后台任务同步命令记录状态；changed=true 表示本次完成了仍在运行的记录。</summary>
    Task<bool> FinishCommandRunByJobAsync(string jobId, string status, long? exitCode, long finishedAt, CancellationToken ct = default);
    Task UpdateCommandRunJobBytesAsync(string jobId, long bytes, CancellationToken ct = default);
    Task<CommandRun?> GetCommandRunAsync(string id, CancellationToken ct = default);
    Task<CommandRun?> GetCommandRunByJobAsync(string jobId, CancellationToken ct = default);
    Task<IReadOnlyList<CommandRun>> ListCommandRunsAsync(CommandRunFilter filter, CancellationToken ct = default);
    Task RecoverInterruptedCommandRunsAsync(long now, CancellationToken ct = default);
    Task ExpireCommandRunOutputsAsync(long cutoff, CancellationToken ct = default);
    Task<IReadOnlyList<string>> DeletableCommandRunOutputIdsAsync(CancellationToken ct = default);
    Task MarkCommandRunOutputsCleanedAsync(IEnumerable<string> ids, CancellationToken ct = default);

    // ---- jobs（jobs.go）----
    Task CreateJobAsync(JobRecord job, CancellationToken ct = default);
    /// <summary>保证后台任务与命令记录的 job_id 同时落库；命令记录不可用时抛 SubSshException。</summary>
    Task CreateJobForCommandRunAsync(JobRecord job, string runId, CancellationToken ct = default);
    Task<JobRecord?> GetJobAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<JobRecord>> ListJobsAsync(long? tokenId = null, long? hostId = null, CancellationToken ct = default);
    /// <summary>未归档主机上的运行中任务（归档主机不再登录确认，也不判失联）。</summary>
    Task<IReadOnlyList<JobRecord>> ListRunningJobsOnActiveHostsAsync(CancellationToken ct = default);
    Task UpdateJobStateAsync(string id, string status, long? exitCode, CancellationToken ct = default);
    Task UpdateJobLogBytesAsync(string id, long bytes, CancellationToken ct = default);
    Task RecordJobCheckAsync(string id, long bytes, long checkedAt, CancellationToken ct = default);
    /// <summary>把 ids 中最后一次确认早于 cutoff 的运行中任务标为 lost，返回被标记的任务。</summary>
    Task<IReadOnlyList<JobRecord>> ExpireUncheckedJobsAsync(IReadOnlyList<string> ids, long cutoff, long now, CancellationToken ct = default);
    /// <summary>删除结束早于 cutoff 的任务记录；返回删除行数。运行中的任务无论多旧都不删。</summary>
    Task<long> PruneFinishedJobsAsync(long cutoff, CancellationToken ct = default);

    // ---- memories（memories.go）----
    Task<long> AddMemoryAsync(MemoryRecord memory, CancellationToken ct = default);
    Task<MemoryRecord?> FindMemoryByContentAsync(long? hostId, string content, CancellationToken ct = default);
    Task<MemoryRecord?> GetMemoryAsync(long id, CancellationToken ct = default);
    /// <summary>更新记忆；不存在时抛 SubSshException。</summary>
    Task UpdateMemoryAsync(MemoryRecord memory, CancellationToken ct = default);
    /// <summary>删除记忆；不存在时抛 SubSshException。</summary>
    Task DeleteMemoryAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryRecord>> ListMemoriesAsync(long? hostId, int limit, int offset, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryAdminRow>> ListMemoriesAdminAsync(long? hostId, string query, int limit, int offset, CancellationToken ct = default);
    /// <summary>FTS5 bm25 相关性检索。</summary>
    Task<IReadOnlyList<MemoryWithRank>> SearchMemoriesFtsAsync(long? hostId, bool includeGlobal, string match, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryRecord>> SearchMemoriesLikeAsync(long? hostId, bool includeGlobal, string query, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryVector>> ListMemoryVectorsAsync(long? hostId, bool includeGlobal, string model, CancellationToken ct = default);
    Task TouchMemoryRecallsAsync(IReadOnlyList<long> ids, long now, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryBankStat>> MemoryStatsAsync(CancellationToken ct = default);
    /// <summary>合并相同正文，返回删除条数。</summary>
    Task<long> DedupeMemoriesAsync(long? hostId, CancellationToken ct = default);
    /// <summary>重要度衰减，返回处理条数。</summary>
    Task<long> DecayMemoriesAsync(long? hostId, long cutoff, CancellationToken ct = default);
    /// <summary>删除长期低重要度未召回记忆，返回删除条数。</summary>
    Task<long> PruneMemoriesAsync(long? hostId, long cutoff, CancellationToken ct = default);

    // ---- oauth（oauth.go）----
    Task<OAuthClientRecord> CreateOAuthClientAsync(OAuthClientRecord client, CancellationToken ct = default);
    Task<OAuthClientRecord?> GetOAuthClientAsync(string clientId, CancellationToken ct = default);
    Task CreateOAuthAuthorizationCodeAsync(OAuthAuthorizationCode code, CancellationToken ct = default);
    /// <summary>授权码交换：无效返回 null；检测到授权码重用时抛 SubSshException（文案与 Go 版一致）。</summary>
    Task<OAuthAuthorizationCode?> ExchangeOAuthAuthorizationCodeAsync(OAuthAuthorizationCodeExchange input, CancellationToken ct = default);
    /// <summary>grant 已被撤销时抛 SubSshException("OAuth grant revoked")。</summary>
    Task CreateOAuthRefreshTokenAsync(OAuthRefreshTokenRecord token, CancellationToken ct = default);
    /// <summary>刷新令牌轮换：无效返回 null；重用抛 SubSshException（文案与 Go 版一致）。</summary>
    Task<OAuthRefreshToken?> RotateOAuthRefreshTokenAsync(OAuthRefreshTokenRotation input, CancellationToken ct = default);
    /// <summary>校验并去重主机 ID；无效/不存在时抛 SubSshException（文案与 Go 版一致）。</summary>
    Task<IReadOnlyList<long>> ValidateHostIdsAsync(IReadOnlyList<long> hostIds, CancellationToken ct = default);

    // ---- observability（observability.go）----
    Task AddAuditAsync(AuditEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> ListAuditAsync(long[] tokenIds, string[] hosts, string[] tools, bool? ok, long before, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListAuditToolsAsync(CancellationToken ct = default);
    Task AddMetricAsync(MetricRecord metric, CancellationToken ct = default);
    Task<MetricRecord?> LatestMetricAsync(long hostId, CancellationToken ct = default);
    Task<IReadOnlyList<MetricRecord>> MetricsSinceAsync(long hostId, long since, CancellationToken ct = default);
    Task CleanupMetricsAsync(long cutoff, CancellationToken ct = default);
}

/// <summary>单个筛选维度上限（对齐 Go store.MaxAuditFilterValues）。</summary>
public static class OneSshStoreLimits
{
    public const int MaxAuditFilterValues = 100;
}
