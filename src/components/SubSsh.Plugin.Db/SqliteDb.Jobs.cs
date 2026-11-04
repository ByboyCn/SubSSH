// SubSsh.Plugin.Db — jobs（对应 Go internal/store/jobs.go）。
using Microsoft.Data.Sqlite;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.Db;

public sealed partial class SqliteDb
{
    // 新任务以启动时刻作为首次确认时间，失联判定从这里起算。
    private const string JobInsert =
        "INSERT INTO jobs(id,host_id,token_id,command,cwd,pid,used_setsid,status,started_at,last_checked_at) VALUES(?,?,?,?,?,?,?,?,?,?)";

    private const string JobColumns =
        "id,host_id,token_id,command,cwd,pid,used_setsid,status,exit_code,log_bytes,started_at,finished_at";

    private static JobRecord ScanJob(SqliteDataReader rd)
    {
        var j = new JobRecord
        {
            Id = SN(rd, 0),
            HostId = IL(rd, 1),
            TokenId = NL(rd, 2),
            Command = SN(rd, 3),
            Cwd = SN(rd, 4),
            Pid = NL(rd, 5),
            UsedSetsid = BO(rd, 6),
            Status = SN(rd, 7),
            ExitCode = NL(rd, 8),
            LogBytes = IL(rd, 9),
            StartedAt = IL(rd, 10),
            FinishedAt = NL(rd, 11),
        };
        return j;
    }

    public Task CreateJobAsync(JobRecord j, CancellationToken ct = default)
    {
        lock (_sync)
        {
            Exec(JobInsert, j.Id, j.HostId, j.TokenId, j.Command, j.Cwd, j.Pid, j.UsedSetsid ? 1 : 0, j.Status, j.StartedAt, j.StartedAt);
        }
        return Task.CompletedTask;
    }

    /// <summary>保证后台任务和命令记录的 job_id 同时落库，避免只创建出其中一边。</summary>
    public Task CreateJobForCommandRunAsync(JobRecord j, string runId, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var tx = _conn.BeginTransaction();
            Exec(tx, JobInsert, j.Id, j.HostId, j.TokenId, j.Command, j.Cwd, j.Pid, j.UsedSetsid ? 1 : 0, j.Status, j.StartedAt, j.StartedAt);
            using (var cmd = tx.Connection!.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE command_runs SET job_id=?,output_available=1 WHERE id=? AND status='running'";
                Bind(cmd, [j.Id, runId]);
                if (cmd.ExecuteNonQuery() != 1)
                {
                    throw new SubSshException("记录不存在");
                }
            }
            tx.Commit();
        }
        return Task.CompletedTask;
    }

    public Task<JobRecord?> GetJobAsync(string id, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var rd = Query("SELECT " + JobColumns + " FROM jobs WHERE id=?", id);
            return Task.FromResult(rd.Read() ? ScanJob(rd) : null);
        }
    }

    public Task<IReadOnlyList<JobRecord>> ListJobsAsync(long? tokenId = null, long? hostId = null, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var q = "SELECT " + JobColumns + " FROM jobs WHERE 1=1";
            var args = new List<object?>();
            if (tokenId != null)
            {
                q += " AND token_id=?";
                args.Add(tokenId);
            }
            if (hostId != null)
            {
                q += " AND host_id=?";
                args.Add(hostId);
            }
            q += " ORDER BY started_at DESC";
            return Task.FromResult<IReadOnlyList<JobRecord>>(ScanJobs(q, args.ToArray()));
        }
    }

    /// <summary>未归档主机上的运行中任务；归档主机任务不刷新也不判失联，取消归档后恢复跟踪。</summary>
    public Task<IReadOnlyList<JobRecord>> ListRunningJobsOnActiveHostsAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<JobRecord>>(ScanJobs(
                "SELECT " + JobColumns + " FROM jobs WHERE status='running'" +
                " AND host_id IN (SELECT id FROM hosts WHERE archived_at IS NULL) ORDER BY started_at DESC"));
        }
    }

    private IReadOnlyList<JobRecord> ScanJobs(string sql, params object?[] args)
    {
        var out_ = new List<JobRecord>();
        using var rd = Query(sql, args);
        while (rd.Read()) out_.Add(ScanJob(rd));
        return out_;
    }

    public Task UpdateJobStateAsync(string id, string status, long? exitCode, CancellationToken ct = default)
    {
        lock (_sync)
        {
            object? finished = status != "running" ? Now() : null;
            Exec("UPDATE jobs SET status=?,exit_code=?,finished_at=? WHERE id=?", status, exitCode, finished, id);
        }
        return Task.CompletedTask;
    }

    public Task UpdateJobLogBytesAsync(string id, long bytes, CancellationToken ct = default)
    {
        lock (_sync)
        {
            Exec("UPDATE jobs SET log_bytes=? WHERE id=?", bytes, id);
        }
        return Task.CompletedTask;
    }

    /// <summary>记下一次成功登录主机确认任务状态：日志大小与确认时间一起更新。</summary>
    public Task RecordJobCheckAsync(string id, long bytes, long checkedAt, CancellationToken ct = default)
    {
        lock (_sync)
        {
            Exec("UPDATE jobs SET log_bytes=?,last_checked_at=? WHERE id=?", bytes, checkedAt, id);
        }
        return Task.CompletedTask;
    }

    /// <summary>把 ids 中最后一次确认早于 cutoff 的运行中任务标为 lost，返回被标记的任务。</summary>
    public Task<IReadOnlyList<JobRecord>> ExpireUncheckedJobsAsync(IReadOnlyList<string> ids, long cutoff, long now, CancellationToken ct = default)
    {
        if (ids.Count == 0) return Task.FromResult<IReadOnlyList<JobRecord>>([]);
        lock (_sync)
        {
            var args = new List<object?> { now, cutoff };
            args.AddRange(ids);
            return Task.FromResult<IReadOnlyList<JobRecord>>(ScanJobs(
                "UPDATE jobs SET status='lost',finished_at=?" +
                " WHERE status='running' AND COALESCE(last_checked_at,started_at)<?" +
                " AND id IN (" + Placeholders(ids.Count) + ") RETURNING " + JobColumns,
                args.ToArray()));
        }
    }

    /// <summary>删除结束早于 cutoff 的任务记录；运行中的任务无论多旧都不删。</summary>
    public Task<long> PruneFinishedJobsAsync(long cutoff, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM jobs WHERE status<>'running' AND COALESCE(finished_at,started_at)<?";
            cmd.Parameters.AddWithValue("@p0", cutoff);
            return Task.FromResult((long)cmd.ExecuteNonQuery());
        }
    }

    /// <summary>Go JobOwnedBy：任务归属于给定令牌。</summary>
    public static bool JobOwnedBy(JobRecord j, long tokenId) => j.TokenId != null && j.TokenId == tokenId;
}
