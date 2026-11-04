// SubSsh.Plugin.Db — command runs（对应 Go internal/store/command_runs.go）。
using Microsoft.Data.Sqlite;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.Db;

public sealed partial class SqliteDb
{
    private const string CommandRunColumns =
        "seq,id,token_id,token_name,tool,host_id,host,command,cwd,session,job_id,status,exit_code," +
        "stdout_preview,stderr_preview,stdout_bytes,stderr_bytes,output_available,output_expired,output_error,error_text,started_at,finished_at";

    private static CommandRun ScanCommandRun(SqliteDataReader rd)
    {
        var run = new CommandRun
        {
            Seq = IL(rd, 0),
            Id = SN(rd, 1),
            TokenId = NL(rd, 2),
            TokenName = ST(rd, 3),
            Tool = SN(rd, 4),
            HostId = NL(rd, 5),
            Host = SN(rd, 6),
            Command = SN(rd, 7),
            Cwd = SN(rd, 8),
            Session = ST(rd, 9),
            JobId = ST(rd, 10),
            Status = SN(rd, 11),
            ExitCode = NL(rd, 12),
            StdoutPreview = SN(rd, 13),
            StderrPreview = SN(rd, 14),
            StdoutBytes = IL(rd, 15),
            StderrBytes = IL(rd, 16),
            OutputAvailable = BO(rd, 17),
            OutputExpired = BO(rd, 18),
            OutputError = ST(rd, 19),
            ErrorText = ST(rd, 20),
            StartedAt = IL(rd, 21),
            FinishedAt = NL(rd, 22),
        };
        return run;
    }

    public Task CreateCommandRunAsync(CommandRun run, CancellationToken ct = default)
    {
        lock (_sync)
        {
            Exec(@"INSERT INTO command_runs(
                id,token_id,token_name,tool,host_id,host,command,cwd,session,status,started_at
            ) VALUES(?,?,?,?,?,?,?,?,?,'running',?)",
                run.Id, run.TokenId, NullableString(run.TokenName ?? ""), run.Tool,
                run.HostId, run.Host, run.Command, run.Cwd, NullableString(run.Session ?? ""), run.StartedAt);
        }
        return Task.CompletedTask;
    }

    public Task FinishCommandRunAsync(string id, CommandRunFinish finish, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"UPDATE command_runs SET
                status=?,exit_code=?,stdout_preview=?,stderr_preview=?,stdout_bytes=?,stderr_bytes=?,
                output_available=?,output_error=?,error_text=?,finished_at=?
                WHERE id=? AND status='running'";
            Bind(cmd,
            [
                finish.Status, finish.ExitCode, finish.StdoutPreview, finish.StderrPreview, finish.StdoutBytes,
                finish.StderrBytes, finish.OutputAvailable ? 1 : 0, NullableString(finish.OutputError ?? ""),
                NullableString(finish.ErrorText ?? ""), finish.FinishedAt, id,
            ]);
            if (cmd.ExecuteNonQuery() == 0)
            {
                throw new SubSshException($"命令执行记录不存在或已经结束: {id}");
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>同步后台任务的最终状态；changed=true 时调用方才发布完成事件，避免重复事件。</summary>
    public Task<bool> FinishCommandRunByJobAsync(string jobId, string status, long? exitCode, long finishedAt, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE command_runs SET status=?,exit_code=?,finished_at=? WHERE job_id=? AND status='running'";
            Bind(cmd, [status, exitCode, finishedAt, jobId]);
            var updated = cmd.ExecuteNonQuery();
            return Task.FromResult(updated > 0);
        }
    }

    public Task UpdateCommandRunJobBytesAsync(string jobId, long bytes, CancellationToken ct = default)
    {
        lock (_sync)
        {
            Exec("UPDATE command_runs SET stdout_bytes=? WHERE job_id=?", bytes, jobId);
        }
        return Task.CompletedTask;
    }

    public Task<CommandRun?> GetCommandRunAsync(string id, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var rd = Query("SELECT " + CommandRunColumns + " FROM command_runs WHERE id=?", id);
            return Task.FromResult(rd.Read() ? ScanCommandRun(rd) : null);
        }
    }

    public Task<CommandRun?> GetCommandRunByJobAsync(string jobId, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var rd = Query("SELECT " + CommandRunColumns + " FROM command_runs WHERE job_id=?", jobId);
            return Task.FromResult(rd.Read() ? ScanCommandRun(rd) : null);
        }
    }

    public Task<IReadOnlyList<CommandRun>> ListCommandRunsAsync(CommandRunFilter filter, CancellationToken ct = default)
    {
        ValidateAuditFilterCount("令牌", filter.TokenIds.Length);
        ValidateAuditFilterCount("主机", filter.Hosts.Length);
        ValidateAuditFilterCount("工具", filter.Tools.Length);
        ValidateAuditFilterCount("状态", filter.Statuses.Length);

        var query = "SELECT " + CommandRunColumns + " FROM command_runs WHERE 1=1";
        var args = new List<object?>();
        if (filter.TokenIds.Length > 0)
        {
            var cond = @"command_runs.token_id=? AND (
                NOT EXISTS (SELECT 1 FROM tokens WHERE tokens.id=?)
                OR EXISTS (SELECT 1 FROM tokens WHERE tokens.id=? AND command_runs.token_name=tokens.name)
            )";
            var conditions = new string[filter.TokenIds.Length];
            for (var i = 0; i < filter.TokenIds.Length; i++)
            {
                conditions[i] = "(" + cond + ")";
                args.Add(filter.TokenIds[i]);
                args.Add(filter.TokenIds[i]);
                args.Add(filter.TokenIds[i]);
            }
            query += " AND (" + string.Join(" OR ", conditions) + ")";
        }
        (query, args) = AppendStringFilter(query, args, "host", filter.Hosts);
        (query, args) = AppendStringFilter(query, args, "tool", filter.Tools);
        (query, args) = AppendStringFilter(query, args, "status", filter.Statuses);
        var needle = filter.Query.Trim();
        if (needle.Length > 0)
        {
            // instr 按普通子串搜索，不会把用户输入的 % / _ 当成 LIKE 通配符。
            query += " AND instr(lower(command || ' ' || host || ' ' || tool || ' ' || id || ' ' || coalesce(token_name,'')), lower(?)) > 0";
            args.Add(needle);
        }
        if (filter.Before > 0)
        {
            query += " AND seq<?";
            args.Add(filter.Before);
        }
        if (filter.Limit <= 0 || filter.Limit > 500)
        {
            filter.Limit = 100;
        }
        query += " ORDER BY seq DESC LIMIT ?";
        args.Add(filter.Limit);

        lock (_sync)
        {
            var out_ = new List<CommandRun>();
            using var rd = Query(query, args.ToArray());
            while (rd.Read())
            {
                out_.Add(ScanCommandRun(rd));
            }
            return Task.FromResult<IReadOnlyList<CommandRun>>(out_);
        }
    }

    private static (string Query, List<object?> Args) AppendStringFilter(string query, List<object?> args, string column, string[] values)
    {
        if (values.Length == 0) return (query, args);
        query += " AND " + column + " IN (" + Placeholders(values.Length) + ")";
        foreach (var v in values) args.Add(v);
        return (query, args);
    }

    /// <summary>网关重启遗留的同步命令标为失联；关联后台任务的记录由 jobs.Manager 刷新。</summary>
    public Task RecoverInterruptedCommandRunsAsync(long now, CancellationToken ct = default)
    {
        lock (_sync)
        {
            Exec("UPDATE command_runs SET status='lost',finished_at=?,error_text='网关在命令完成前重启，无法确认最终状态' WHERE status='running' AND job_id IS NULL", now);
        }
        return Task.CompletedTask;
    }

    public Task ExpireCommandRunOutputsAsync(long cutoff, CancellationToken ct = default)
    {
        lock (_sync)
        {
            Exec(@"UPDATE command_runs SET stdout_preview='',stderr_preview='',
                output_expired=CASE WHEN output_available=1 THEN 1 ELSE output_expired END,
                output_available=0
                WHERE job_id IS NULL AND status<>'running' AND finished_at<?
                AND (output_available=1 OR stdout_preview<>'' OR stderr_preview<>'')", cutoff);
        }
        return Task.CompletedTask;
    }

    /// <summary>数据库明确判定不再需要本地文件的同步命令 ID。</summary>
    public Task<IReadOnlyList<string>> DeletableCommandRunOutputIdsAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            var ids = new List<string>();
            using var rd = Query(@"SELECT id FROM command_runs
                WHERE job_id IS NULL AND status<>'running' AND output_cleaned=0 AND (output_expired=1 OR output_available=0)");
            while (rd.Read()) ids.Add(rd.GetString(0));
            return Task.FromResult<IReadOnlyList<string>>(ids);
        }
    }

    /// <summary>文件删除成功或确认文件不存在后收敛清理状态（分批 500）。</summary>
    public Task MarkCommandRunOutputsCleanedAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var values = ids.ToList();
        if (values.Count == 0) return Task.CompletedTask;
        lock (_sync)
        {
            using var tx = _conn.BeginTransaction();
            const int batchSize = 500;
            for (var start = 0; start < values.Count; start += batchSize)
            {
                var end = Math.Min(start + batchSize, values.Count);
                var batch = values[start..end];
                Exec(tx, "UPDATE command_runs SET output_cleaned=1 WHERE id IN (" + Placeholders(batch.Count) + ")", batch.Cast<object?>().ToArray());
            }
            tx.Commit();
        }
        return Task.CompletedTask;
    }
}
