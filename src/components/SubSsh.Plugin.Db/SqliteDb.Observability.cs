// SubSsh.Plugin.Db — 审计与监控指标（对应 Go internal/store/observability.go）。
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.Db;

public sealed partial class SqliteDb
{
    /// <summary>单个筛选维度上限（对齐 Go MaxAuditFilterValues；token 条件每项占 3 个变量）。</summary>
    public const int MaxAuditFilterValues = OneSshStoreLimits.MaxAuditFilterValues;

    private static void ValidateAuditFilterCount(string name, int count)
    {
        if (count > MaxAuditFilterValues)
        {
            throw new SubSshException($"审计{name}筛选最多 {MaxAuditFilterValues} 项");
        }
    }

    public Task AddAuditAsync(AuditEntry a, CancellationToken ct = default)
    {
        lock (_sync)
        {
            object? runIds = a.RunIds is { Length: > 0 } ? JsonSerializer.Serialize(a.RunIds) : null;
            Exec(@"INSERT INTO audit(ts,token_id,token_name,tool,host,params_json,command_run_ids_json,ok,exit_code,duration_ms,bytes_out)
                VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                a.Ts, a.TokenId, NullableString(a.TokenName ?? ""), a.Tool, NullableString(a.Host ?? ""),
                a.ParamsJson, runIds, a.Ok ? 1 : 0, a.ExitCode, a.DurationMs, a.BytesOut);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ListAuditAsync(long[] tokenIds, string[] hosts, string[] tools, bool? ok, long before, int limit, CancellationToken ct = default)
    {
        ValidateAuditFilterCount("令牌", tokenIds.Length);
        ValidateAuditFilterCount("主机", hosts.Length);
        ValidateAuditFilterCount("工具", tools.Length);

        var q = "SELECT id,ts,token_id,token_name,tool,host,params_json,command_run_ids_json,ok,exit_code,duration_ms,bytes_out FROM audit WHERE 1=1";
        var args = new List<object?>();
        if (tokenIds.Length > 0)
        {
            // 迁移前的 ID 可能已被复用；当前令牌存在时，只接受写入时已快照其名称的记录。多值是 OR 关系。
            var cond = @"audit.token_id=? AND (
                NOT EXISTS (SELECT 1 FROM tokens WHERE tokens.id=?)
                OR EXISTS (
                    SELECT 1 FROM tokens
                    WHERE tokens.id=? AND audit.token_name=tokens.name
                )
            )";
            var conds = new string[tokenIds.Length];
            for (var i = 0; i < tokenIds.Length; i++)
            {
                conds[i] = "(" + cond + ")";
                args.Add(tokenIds[i]);
                args.Add(tokenIds[i]);
                args.Add(tokenIds[i]);
            }
            q += " AND (" + string.Join(" OR ", conds) + ")";
        }
        if (hosts.Length > 0)
        {
            q += " AND host IN (" + Placeholders(hosts.Length) + ")";
            foreach (var h in hosts) args.Add(h);
        }
        if (tools.Length > 0)
        {
            q += " AND tool IN (" + Placeholders(tools.Length) + ")";
            foreach (var t in tools) args.Add(t);
        }
        if (ok != null)
        {
            q += " AND ok=?";
            args.Add(ok.Value ? 1 : 0);
        }
        if (before > 0)
        {
            q += " AND id<?";
            args.Add(before);
        }
        if (limit <= 0 || limit > 500)
        {
            limit = 100;
        }
        q += " ORDER BY id DESC LIMIT ?";
        args.Add(limit);

        lock (_sync)
        {
            var out_ = new List<AuditEntry>();
            using var rd = Query(q, args.ToArray());
            while (rd.Read())
            {
                var entry = new AuditEntry
                {
                    Id = IL(rd, 0),
                    Ts = IL(rd, 1),
                    TokenId = NL(rd, 2),
                    TokenName = ST(rd, 3),
                    Tool = SN(rd, 4),
                    Host = ST(rd, 5),
                    ParamsJson = SN(rd, 6),
                    RunIds = rd.IsDBNull(7) ? null : (JsonSerializer.Deserialize<string[]>(SN(rd, 7))
                        ?? throw new SubSshException("解析审计命令执行关联")),
                    Ok = BO(rd, 8),
                    ExitCode = NL(rd, 9),
                    DurationMs = IL(rd, 10),
                    BytesOut = IL(rd, 11),
                };
                out_.Add(entry);
            }
            return Task.FromResult<IReadOnlyList<AuditEntry>>(out_);
        }
    }

    public Task<IReadOnlyList<string>> ListAuditToolsAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            var tools = new List<string>();
            using var rd = Query("SELECT DISTINCT tool FROM audit WHERE tool<>'' ORDER BY tool");
            while (rd.Read()) tools.Add(rd.GetString(0));
            return Task.FromResult<IReadOnlyList<string>>(tools);
        }
    }

    public Task AddMetricAsync(MetricRecord m, CancellationToken ct = default)
    {
        lock (_sync)
        {
            Exec("INSERT OR REPLACE INTO metrics(host_id,ts,cpu_pct,mem_used_kb,mem_total_kb,load1,disks_json) VALUES(?,?,?,?,?,?,?)",
                m.HostId, m.Ts, m.CpuPct, m.MemUsedKb, m.MemTotalKb, m.Load1, m.DisksJson);
        }
        return Task.CompletedTask;
    }

    public Task<MetricRecord?> LatestMetricAsync(long hostId, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var rd = Query("SELECT host_id,ts,cpu_pct,mem_used_kb,mem_total_kb,load1,disks_json FROM metrics WHERE host_id=? ORDER BY ts DESC LIMIT 1", hostId);
            return Task.FromResult(rd.Read() ? ScanMetric(rd) : null);
        }
    }

    public Task<IReadOnlyList<MetricRecord>> MetricsSinceAsync(long hostId, long since, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var out_ = new List<MetricRecord>();
            using var rd = Query("SELECT host_id,ts,cpu_pct,mem_used_kb,mem_total_kb,load1,disks_json FROM metrics WHERE host_id=? AND ts>=? ORDER BY ts", hostId, since);
            while (rd.Read()) out_.Add(ScanMetric(rd));
            return Task.FromResult<IReadOnlyList<MetricRecord>>(out_);
        }
    }

    public Task CleanupMetricsAsync(long cutoff, CancellationToken ct = default)
    {
        lock (_sync)
        {
            Exec("DELETE FROM metrics WHERE ts<?", cutoff);
        }
        return Task.CompletedTask;
    }

    private static MetricRecord ScanMetric(SqliteDataReader rd) => new()
    {
        HostId = IL(rd, 0),
        Ts = IL(rd, 1),
        CpuPct = DB(rd, 2),
        MemUsedKb = NL(rd, 3),
        MemTotalKb = NL(rd, 4),
        Load1 = DB(rd, 5),
        DisksJson = SN(rd, 6),
    };
}
