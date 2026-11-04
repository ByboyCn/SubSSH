// SubSsh.Plugin.WebApi — WebApiStoreAdapter：把公共存储组件 ISubSshStoreFull（Plugin.Db 提供）
// 适配为 WebApi 所需的 IWebApiStore 窄接口。模型转换逐字段对齐两侧定义：
//   WebApiHostView  ↔ HostConfig.View()（HostViewConfig）
//   WebApiKeyView   ↔ SshKey
//   WebApiToken     ↔ ApiToken（视图子集，不含 LastUsedAt/ExpiresAt/Resource/ClientId）
//   WebApiTokenCreate/Update ↔ TokenCreate/TokenUpdate
//   WebApiJob       ↔ JobRecord（视图子集，不含 TokenId）
//   WebApiAuditView ↔ AuditEntry（Go 默认 JSON 键名 ID/Ts/...）
//   WebApiMetric    ↔ MetricRecord
//   WebApiMemoryAdminRow ↔ MemoryAdminRow（视图子集，不含 LastRecalled）
//   WebApiMemoryBankStatRaw ↔ MemoryBankStat
//   WebApiCommandRun ↔ CommandRun（视图附加 duration_ms 派生字段）
// 错误语义：store 返回 null / 抛 SubSshException 的场景按 IWebApiStore 契约转换为 WebApiHostException。
namespace SubSsh.Plugin.WebApi;

using SubSsh.Core.Abstractions;

public sealed class WebApiStoreAdapter(ISubSshStoreFull store) : IWebApiStore
{
    private readonly ISubSshStoreFull _store = store;

    // ---------- 主机 ----------

    public async Task<IReadOnlyList<WebApiHostView>> ListHostsAsync(CancellationToken ct = default)
    {
        var hosts = await _store.ListHostsAsync(ct);
        return hosts.Select(ToView).ToList();
    }

    public async Task<WebApiHostView> GetHostAsync(long id, CancellationToken ct = default)
    {
        var host = await _store.GetHostAsync(id, ct);
        return host is null ? throw NotFound($"主机不存在: {id}") : ToView(host);
    }

    public async Task<WebApiHostView> GetHostByNameAsync(string name, CancellationToken ct = default)
    {
        var host = await _store.GetHostByNameAsync(name, ct);
        return host is null ? throw NotFound($"主机不存在: {name}") : ToView(host);
    }

    // ---------- 密钥 ----------

    public async Task<IReadOnlyList<WebApiKeyView>> ListKeysAsync(CancellationToken ct = default)
    {
        var keys = await _store.ListKeysAsync(ct);
        return keys.Select(ToView).ToList();
    }

    /// <summary>privateKeyEnc 已是 ICryptoBox 加密后的密文，store 层只存不解释——原样透传。</summary>
    public async Task<WebApiKeyView> CreateKeyAsync(string name, byte[] privateKeyEnc, string publicKey, CancellationToken ct = default)
    {
        var key = await _store.CreateKeyAsync(name, privateKeyEnc, publicKey, ct);
        return ToView(key);
    }

    public Task DeleteKeyAsync(long id, CancellationToken ct = default) => _store.DeleteKeyAsync(id, ct);

    // ---------- 令牌 ----------

    public async Task<IReadOnlyList<WebApiToken>> ListTokensAsync(CancellationToken ct = default)
    {
        var tokens = await _store.ListTokensAsync(ct);
        return tokens.Select(ToView).ToList();
    }

    public async Task<long[]> TokenHostIdsAsync(long tokenId, CancellationToken ct = default)
    {
        var ids = await _store.TokenHostIdsAsync(tokenId, ct);
        return [.. ids];
    }

    public async Task<WebApiToken> CreateTokenAsync(WebApiTokenCreate create, CancellationToken ct = default)
    {
        var token = await _store.CreateTokenAsync(new TokenCreate
        {
            Name = create.Name,
            Hash = create.Hash,
            AllHosts = create.AllHosts,
            ManageHosts = create.ManageHosts,
            DisabledTools = create.DisabledTools,
            HostIds = create.HostIds,
            // WebApi 管理面创建的令牌来源固定为 manual；ExpiresAt=0 表示不过期；OAuth 专属字段留空。
            Source = "manual",
            ExpiresAt = 0,
            Resource = "",
            ClientId = "",
        }, ct);
        return ToView(token);
    }

    public async Task<WebApiToken> UpdateTokenAsync(long id, WebApiTokenUpdate update, CancellationToken ct = default)
    {
        var token = await _store.UpdateTokenAsync(id, new TokenUpdate
        {
            Name = update.Name,
            AllHosts = update.AllHosts,
            ManageHosts = update.ManageHosts,
            HostIds = update.HostIds,
            DisabledTools = update.DisabledTools,
        }, ct);
        // store：不存在时抛 SubSshException；按 WebApi 契约转 NotFound。
        return ToView(token ?? throw NotFound($"令牌不存在: {id}"));
    }

    public Task DeleteTokenAsync(long id, CancellationToken ct = default) => _store.DeleteTokenAsync(id, ct);

    public async Task<long[]> ValidateHostIdsAsync(long[] hostIds, CancellationToken ct = default)
    {
        try
        {
            var ids = await _store.ValidateHostIdsAsync(hostIds, ct);
            return [.. ids];
        }
        catch (SubSshException ex)
        {
            // store：无效/不存在抛 SubSshException；WebApi 契约为 Invalid（HTTP 400）。
            throw new WebApiHostException(WebApiHostErrorKind.Invalid, ex.Message);
        }
    }

    // ---------- 任务 ----------

    public async Task<IReadOnlyList<WebApiJob>> ListJobsAsync(long? hostId, CancellationToken ct = default)
    {
        var jobs = await _store.ListJobsAsync(tokenId: null, hostId: hostId, ct);
        return jobs.Select(ToView).ToList();
    }

    public async Task<WebApiJob> GetJobAsync(string id, CancellationToken ct = default)
    {
        var job = await _store.GetJobAsync(id, ct);
        return job is null ? throw NotFound($"任务不存在: {id}") : ToView(job);
    }

    // ---------- 审计 ----------

    public async Task<IReadOnlyList<WebApiAuditView>> ListAuditAsync(long[] tokenIds, string[] hosts, string[] tools, bool? ok, long before, int limit, CancellationToken ct = default)
    {
        var entries = await _store.ListAuditAsync(tokenIds, hosts, tools, ok, before, limit, ct);
        return entries.Select(ToView).ToList();
    }

    public Task<IReadOnlyList<string>> ListAuditToolsAsync(CancellationToken ct = default) => _store.ListAuditToolsAsync(ct);

    // ---------- 监控 ----------

    public async Task<IReadOnlyList<WebApiMetric>> MetricsSinceAsync(long hostId, long sinceTsMs, CancellationToken ct = default)
    {
        var metrics = await _store.MetricsSinceAsync(hostId, sinceTsMs, ct);
        return metrics.Select(ToView).ToList();
    }

    // ---------- 记忆 ----------

    public async Task<IReadOnlyList<WebApiMemoryAdminRow>> ListMemoriesAdminAsync(long? hostId, string query, int limit, int offset, CancellationToken ct = default)
    {
        var rows = await _store.ListMemoriesAdminAsync(hostId, query, limit, offset, ct);
        return rows.Select(ToView).ToList();
    }

    public async Task DeleteMemoryAsync(long id, CancellationToken ct = default)
    {
        // store：不存在时抛 SubSshException；先探测再删，保证按 WebApi 契约抛 NotFound。
        if (await _store.GetMemoryAsync(id, ct) is null)
        {
            throw NotFound($"记忆不存在: {id}");
        }

        await _store.DeleteMemoryAsync(id, ct);
    }

    public async Task<IReadOnlyList<WebApiMemoryBankStatRaw>> MemoryStatsAsync(CancellationToken ct = default)
    {
        var stats = await _store.MemoryStatsAsync(ct);
        return stats.Select(s => new WebApiMemoryBankStatRaw(s.HostId, s.Count, s.Embedded, s.LastWritten)).ToList();
    }

    // ---------- 命令记录 ----------

    public async Task<WebApiCommandRun> GetCommandRunAsync(string id, CancellationToken ct = default)
    {
        var run = await _store.GetCommandRunAsync(id, ct);
        return run is null ? throw NotFound($"命令记录不存在: {id}") : ToView(run);
    }

    public async Task<IReadOnlyList<WebApiCommandRun>> ListCommandRunsAsync(WebApiCommandRunFilter filter, CancellationToken ct = default)
    {
        var runs = await _store.ListCommandRunsAsync(new CommandRunFilter
        {
            TokenIds = filter.TokenIds,
            Hosts = filter.Hosts,
            Tools = filter.Tools,
            Statuses = filter.Statuses,
            Query = filter.Query,
            Before = filter.Before,
            Limit = filter.Limit,
        }, ct);
        return runs.Select(ToView).ToList();
    }

    // ---------- 模型转换 ----------

    private static WebApiHostException NotFound(string message) => new(WebApiHostErrorKind.NotFound, message);

    private static WebApiHostView ToView(HostConfig host) => new()
    {
        Id = host.Id,
        Name = host.Name,
        Addr = host.Addr,
        Port = host.Port,
        Username = host.Username,
        AuthType = host.AuthType,
        KeyId = host.KeyId,
        HostKeyFp = host.HostKeyFp,
        JumpHostId = host.JumpHostId,
        MonitorEnabled = host.MonitorEnabled,
        Tags = host.Tags,
        CreatedAt = host.CreatedAt,
        Archived = host.ArchivedAt != null,
        ArchivedAt = host.ArchivedAt,
    };

    private static WebApiKeyView ToView(SshKey key) => new(key.Id, key.Name, key.PublicKey, key.CreatedAt);

    private static WebApiToken ToView(ApiToken token) => new()
    {
        Id = token.Id,
        Name = token.Name,
        AllHosts = token.AllHosts,
        ManageHosts = token.ManageHosts,
        DisabledTools = token.DisabledTools,
        Source = token.Source,
        CreatedAt = token.CreatedAt,
    };

    private static WebApiJob ToView(JobRecord job) => new()
    {
        Id = job.Id,
        HostId = job.HostId,
        Command = job.Command,
        Cwd = job.Cwd,
        Pid = job.Pid,
        UsedSetsid = job.UsedSetsid,
        Status = job.Status,
        ExitCode = job.ExitCode,
        LogBytes = job.LogBytes,
        StartedAt = job.StartedAt,
        FinishedAt = job.FinishedAt,
    };

    private static WebApiAuditView ToView(AuditEntry entry) => new()
    {
        Id = entry.Id,
        Ts = entry.Ts,
        TokenId = entry.TokenId,
        TokenName = entry.TokenName,
        Tool = entry.Tool,
        Host = entry.Host,
        ParamsJson = entry.ParamsJson,
        RunIds = entry.RunIds,
        Ok = entry.Ok,
        ExitCode = entry.ExitCode,
        DurationMs = entry.DurationMs,
        BytesOut = entry.BytesOut,
    };

    private static WebApiMetric ToView(MetricRecord metric) => new(metric.HostId, metric.Ts, metric.CpuPct, metric.MemUsedKb, metric.MemTotalKb, metric.Load1, metric.DisksJson);

    private static WebApiMemoryAdminRow ToView(MemoryAdminRow row) => new()
    {
        Id = row.Id,
        HostId = row.HostId,
        HostName = row.HostName,
        Content = row.Content,
        Source = row.Source,
        Importance = row.Importance,
        Veracity = row.Veracity,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        RecallCount = row.RecallCount,
    };

    private static WebApiCommandRun ToView(CommandRun run) => new()
    {
        Seq = run.Seq,
        Id = run.Id,
        TokenId = run.TokenId,
        TokenName = run.TokenName,
        Tool = run.Tool,
        HostId = run.HostId,
        Host = run.Host,
        Command = run.Command,
        Cwd = run.Cwd,
        Session = run.Session,
        JobId = run.JobId,
        Status = run.Status,
        ExitCode = run.ExitCode,
        StdoutBytes = run.StdoutBytes,
        StderrBytes = run.StderrBytes,
        OutputAvailable = run.OutputAvailable,
        // OutputExpired 由输出文件清理方维护，store 记录未建模，视图恒为 false（已列入移植差异）。
        OutputExpired = false,
        OutputError = run.OutputError,
        Error = run.ErrorText,
        StartedAt = run.StartedAt,
        FinishedAt = run.FinishedAt,
        StdoutPreview = run.StdoutPreview,
        StderrPreview = run.StderrPreview,
        DurationMs = run.FinishedAt is { } finished ? finished - run.StartedAt : 0,
    };
}
