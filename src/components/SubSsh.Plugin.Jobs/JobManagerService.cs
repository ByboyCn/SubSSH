// SubSsh.Plugin.Jobs — JobManagerService：jobs/manager.go + maintenance.go 全量 C# 移植。
// 语义 1:1：setsid+nohup 启动脚本与 argv 标记身份核验（jobProcessIdentityScript）、
// orphan 清理（远端进程已启动但本地事务失败时先核验再 TERM/KILL）、
// RefreshAll 按主机分组、每主机一个 worker、8 秒整批硬预算、挂住主机登记 stuck、
// Kill 的 TERM→KILL 阶梯在远端脚本内完成（TERM 等 3 秒后若仍存活则报 running），
// 维护循环首轮 1 分钟、之后每小时，7 天未确认标 lost、30 天保留期。
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using SubSsh.Core.Abstractions;
using SubSsh.Core.Events;

namespace SubSsh.Plugin.Jobs;

public sealed class JobManagerService : IJobManager
{
    // 列表刷新的时间预算与主机并发度。失联主机的 TCP 建连、SSH 握手各自最多等 15 秒，
    // 而连接池对同一主机串行拨号：逐个刷新时一台离线主机上的 N 个任务就要等 N×30 秒。
    private static readonly TimeSpan ListRefreshTimeout = TimeSpan.FromSeconds(8);
    private const int RefreshHosts = 8;

    // maintenance.go：维护时每台主机自己的探测预算；首轮延后、之后每小时。
    private static readonly TimeSpan MaintenanceHostBudget = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaintenanceFirstDelay = TimeSpan.FromMinutes(1);

    /// <summary>运行中任务连续无法登录主机确认的上限，超过即标为 lost（UnconfirmedLostAfter）。</summary>
    private static readonly TimeSpan UnconfirmedLostAfter = TimeSpan.FromDays(7);
    /// <summary>已结束任务记录的保留期（FinishedRetention）。</summary>
    private static readonly TimeSpan FinishedRetention = TimeSpan.FromDays(30);

    private readonly IConnectionPool _pool;
    private readonly ICommandRunner _exec;
    private IOneSshStoreFull _store = null!; // 由 JobsPlugin 经 AttachStore 注入（IOneSshStoreFull Require 晚于构造）。

    /// <summary>任务事件总线（job_status / command_job_linked / command_finished）。</summary>
    public EventBus Events { get; } = new();

    // stuck 记录超过截止仍未结束的列表刷新 worker：key 为 host ID，
    // value 是它结束时完成的 TaskCompletionSource（对应 Go sync.Map 里的 done channel）。
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _stuck = new();

    // 维护循环的生命周期（对应 Go m.cancel）。
    private readonly object _maintenanceGate = new();
    private CancellationTokenSource? _maintenanceCts;

    // 维护用并发槽（每轮新建；对应 Go probeRunning 的 sem）。
    private readonly SemaphoreSlim _maintenanceSem = new(RefreshHosts);

    public JobManagerService(IConnectionPool pool, ICommandRunner exec)
    {
        _pool = pool;
        _exec = exec;
        _store = null!; // 由 JobsPlugin 经 AttachStore 注入（IOneSshStoreFull Require 晚于构造）。
    }

    /// <summary>由插件入口注入存储（对应 Go Manager.Store 字段）。</summary>
    public void AttachStore(IOneSshStoreFull store) => _store = store;

    private static string TimeoutReason() => "8s 内未能从主机确认状态";

    private static string Shq(string s) => "'" + s.Replace("'", "'\\''") + "'";

    // ------------------------------------------------------------------
    // 启动（Go Start / StartTracked / start）
    // ------------------------------------------------------------------

    public Task<JobRecord> StartAsync(HostConfig host, long tokenId, string command, string cwd,
        IReadOnlyDictionary<string, string>? env, CancellationToken ct = default)
        => StartCoreAsync(host, tokenId, command, cwd, env, "", ct);

    public Task<JobRecord> StartTrackedAsync(HostConfig host, long tokenId, string command, string cwd,
        IReadOnlyDictionary<string, string>? env, string runId, CancellationToken ct = default)
        => StartCoreAsync(host, tokenId, command, cwd, env, runId, ct);

    private async Task<JobRecord> StartCoreAsync(HostConfig host, long tokenId, string command, string cwd,
        IReadOnlyDictionary<string, string>? env, string runId, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("D");
        var cwdShell = Shq(cwd);
        if (cwd == "~")
        {
            cwdShell = "\"$HOME\"";
        }
        var inner = TrackedJobCommand(command);
        var script = "d=\"$HOME/.onessh/jobs/" + id + "\"; export d; ONESSH_JOB_MARKER=" + Shq("onessh-job-" + id)
            + "; export ONESSH_JOB_MARKER; mkdir -p \"$d\"; cd " + cwdShell + " || exit 97; "
            + "if command -v setsid >/dev/null 2>&1; then S=setsid; else S=; fi; $S nohup sh -c "
            + Shq(inner) + " " + Shq("onessh-job-" + id)
            + " >\"$d/out.log\" 2>&1 </dev/null & echo \"$!:$S\"";
        var client = await _pool.GetAsync(host.Name, ct).ConfigureAwait(false);
        ExecResult res;
        try
        {
            res = await _exec.RunAsync(client, script, "~", env,
                new ExecOptions { Timeout = TimeSpan.FromSeconds(15), MaxLines = 20, Tail = true }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        if (res.ExitCode != 0)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new SubSshException("启动任务失败: " + res.Output);
        }
        var line = res.Output.Trim();
        var parts = line.Split(':');
        if (!long.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new SubSshException($"无法解析任务 PID: \"{line}\"");
        }
        var j = new JobRecord
        {
            Id = id,
            HostId = host.Id,
            TokenId = tokenId > 0 ? tokenId : null,
            Command = command,
            Cwd = cwd,
            Pid = pid,
            UsedSetsid = parts.Length > 1 && parts[1].Trim() == "setsid",
            Status = "running",
            StartedAt = DateTimeOffset.Now.ToUnixTimeSeconds(),
        };
        try
        {
            if (runId.Length != 0)
            {
                await _store.CreateJobForCommandRunAsync(j, runId, ct).ConfigureAwait(false);
            }
            else
            {
                await _store.CreateJobAsync(j, ct).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            // 远端进程已经启动，但本地事务没有落成时不能把它变成无人可见、无法终止的孤儿。
            // 仍须先核验 job 标记；短命任务退出并发生 PID 复用时绝不能误伤新进程。
            try
            {
                await CleanupStartedJobAsync(client, j, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // 尽力清理；原始落库错误仍然抛出。
            }
            throw;
        }
        Events.Publish(new JobStatusEvent(id, "running"));
        if (runId.Length != 0)
        {
            Events.Publish(new CommandJobLinkedEvent(runId, id));
        }
        return j;
    }

    private async Task CleanupStartedJobAsync(IRemoteConnection client, JobRecord j, CancellationToken ct)
    {
        var cleanupScript = JobProcessIdentityScript(j) + "\n" +
            """
            if alive && owned; then
              kill -TERM "$target" 2>/dev/null || :
              i=0
              while alive && [ "$i" -lt 10 ]; do sleep 0.1; i=$((i+1)); done
              if alive && owned; then kill -KILL "$target" 2>/dev/null || :; fi
            fi
            """;
        await _exec.RunAsync(client, cleanupScript, "~", null,
            new ExecOptions { Timeout = TimeSpan.FromSeconds(5), MaxLines = 5 }, ct).ConfigureAwait(false);
    }

    /// <summary>在子 shell 中执行用户命令，确保 command 自己调用 exit 或启用 set -e 时，
    /// 外层仍能写入真实退出码（Go trackedJobCommand）。</summary>
    private static string TrackedJobCommand(string command)
        => "( " + command + "\n); __ec=$?; echo \"$__ec\" > \"$d/exit\"";

    /// <summary>
    /// 以 argv 标记验证进程组 leader；leader 已退出时，再用继承的环境标记核验同 PGID 的存活成员。
    /// 宁可把无法证明归属的任务记为 lost，也绝不向未知 PID 发信号（Go jobProcessIdentityScript）。
    /// </summary>
    private static string JobProcessIdentityScript(JobRecord j)
    {
        var pid = (j.Pid ?? 0).ToString(CultureInfo.InvariantCulture);
        var target = pid;
        var group = "0";
        if (j.UsedSetsid)
        {
            target = "-" + pid;
            group = "1";
        }
        var marker = "onessh-job-" + j.Id;
        return "pid=" + Shq(pid) + "; target=" + Shq(target) + "; marker=" + Shq(marker) + "; group=" + group + ";\n" +
            "owned() {\n" +
            "  args=$(ps -p \"$pid\" -o args= 2>/dev/null) || args=\n" +
            "  if [ -z \"$args\" ] && [ -r \"/proc/$pid/cmdline\" ]; then\n" +
            "    args=$(tr '\\0' ' ' < \"/proc/$pid/cmdline\" 2>/dev/null) || args=\n" +
            "  fi\n" +
            "  case \"$args\" in *\"$marker\"*) return 0;; esac\n" +
            "  [ \"$group\" = 1 ] || return 1\n" +
            "  for proc in /proc/[0-9]*; do\n" +
            "    [ -r \"$proc/stat\" ] && [ -r \"$proc/environ\" ] || continue\n" +
            "    stat=$(cat \"$proc/stat\" 2>/dev/null) || continue\n" +
            "    stat=${stat##*) }\n" +
            "    set -- $stat\n" +
            "    [ \"$3\" = \"$pid\" ] || continue\n" +
            "    if tr '\\0' '\\n' < \"$proc/environ\" 2>/dev/null | grep -Fqx \"ONESSH_JOB_MARKER=$marker\"; then return 0; fi\n" +
            "  done\n" +
            "  return 1\n" +
            "}\n" +
            "alive() { kill -0 \"$target\" 2>/dev/null; }";
    }

    // ------------------------------------------------------------------
    // 刷新（Go Refresh / RefreshAll / refreshHost / probe / snapshot / staleStatus）
    // ------------------------------------------------------------------

    public async Task<JobStatus> RefreshAsync(JobRecord job, CancellationToken ct = default)
    {
        if (job.Status != "running")
        {
            return Snapshot(job);
        }
        var client = await ActiveHostClientAsync(job.HostId, ct).ConfigureAwait(false);
        return await ProbeAsync(client, job, ct).ConfigureAwait(false);
    }

    /// <summary>取得未归档主机的连接；归档主机不建立连接。</summary>
    private async Task<IRemoteConnection> ActiveHostClientAsync(long hostId, CancellationToken ct)
    {
        var h = await _store.GetHostAsync(hostId, ct).ConfigureAwait(false)
            ?? throw new SubSshException($"主机不存在: {hostId}");
        if (h.ArchivedAt != null)
        {
            throw new SubSshException("主机已归档，不再刷新任务状态");
        }
        return await _pool.GetAsync(h.Name, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JobStatus>> RefreshAllAsync(IReadOnlyList<JobRecord> list, CancellationToken ct = default)
    {
        var outList = new JobStatus[list.Count];
        var byHost = new Dictionary<long, List<int>>();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Status != "running")
            {
                outList[i] = Snapshot(list[i]);
                continue;
            }
            // 先按超时填好快照，worker 按时交付的结果再覆盖
            outList[i] = StaleStatus(list[i], TimeoutReason());
            if (!byHost.TryGetValue(list[i].HostId, out var idx))
            {
                byHost[list[i].HostId] = idx = new List<int>();
            }
            idx.Add(i);
        }
        if (byHost.Count == 0)
        {
            return outList;
        }
        var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(ListRefreshTimeout);
        // 截止是硬的：连接池的主机锁、NewSession、StartAsync 都不响应取消，
        // 半死的连接能把 worker 永久挂住，所以到点直接返回快照，不等 worker 收尾；
        // 返回后迟到的结果通过 delivered 标记丢弃。
        var delivered = false;
        var sem = new SemaphoreSlim(RefreshHosts);
        var pending = new Dictionary<long, TaskCompletionSource>();
        try
        {
            foreach (var (hostId, idx) in byHost)
            {
                if (HostStuck(hostId))
                {
                    foreach (var i in idx)
                    {
                        outList[i] = StaleStatus(list[i], "上一次刷新这台主机的请求仍未返回");
                    }
                    continue;
                }
                var jobs = new JobRecord[idx.Count];
                for (var k = 0; k < idx.Count; k++) jobs[k] = list[idx[k]];
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pending[hostId] = done;
                _ = Task.Run(async () =>
                {
                    var acquired = false;
                    try
                    {
                        try
                        {
                            await sem.WaitAsync(deadlineCts.Token).ConfigureAwait(false);
                            acquired = true;
                        }
                        catch (OperationCanceledException)
                        {
                            // 没轮到就到了截止：已按预填的超时快照返回
                            return;
                        }
                        var statuses = await RefreshHostAsync(deadlineCts.Token, hostId, jobs).ConfigureAwait(false);
                        if (!Volatile.Read(ref delivered))
                        {
                            lock (outList)
                            {
                                if (!Volatile.Read(ref delivered))
                                {
                                    for (var k = 0; k < idx.Count; k++) outList[idx[k]] = statuses[k];
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (acquired) sem.Release();
                        done.TrySetResult();
                    }
                });
            }
            try
            {
                await Task.WhenAll(pending.Values.Select(p => p.Task))
                    .WaitAsync(deadlineCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                foreach (var (hostId, done) in pending)
                {
                    if (!done.Task.IsCompleted) _stuck[hostId] = done;
                }
            }
        }
        finally
        {
            Volatile.Write(ref delivered, true);
            deadlineCts.Dispose();
        }
        return outList;
    }

    /// <summary>判断这台主机是否还挂着之前被放弃的 worker；已结束的记录顺手清掉（Go hostStuck）。</summary>
    private bool HostStuck(long hostId)
    {
        if (!_stuck.TryGetValue(hostId, out var done)) return false;
        if (done.Task.IsCompleted)
        {
            _stuck.TryRemove(KeyValuePair.Create(hostId, done));
            return false;
        }
        return true;
    }

    /// <summary>刷新同一台主机上的运行中任务，只取一次连接（Go refreshHost）。它可能在调用方
    /// 返回后仍继续运行，因此只读写自己的参数。</summary>
    private async Task<JobStatus[]> RefreshHostAsync(CancellationToken token, long hostId, IReadOnlyList<JobRecord> jobs)
    {
        var outStatuses = new JobStatus[jobs.Count];
        IRemoteConnection? client = null;
        string? clientErr = null;
        try
        {
            client = await ActiveHostClientAsync(hostId, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            clientErr = Reason(ex, token);
        }
        for (var k = 0; k < jobs.Count; k++)
        {
            if (clientErr != null)
            {
                outStatuses[k] = StaleStatus(jobs[k], clientErr);
                continue;
            }
            try
            {
                outStatuses[k] = await ProbeAsync(client!, jobs[k], token).ConfigureAwait(false);
            }
            catch (Exception probeErr)
            {
                outStatuses[k] = StaleStatus(jobs[k], Reason(probeErr, token));
            }
        }
        return outStatuses;
    }

    /// <summary>超出预算时底层错误五花八门（i/o timeout、operation was canceled、deadline exceeded），
    /// 统一退回超时文案（对应 Go refreshHost.fail）。</summary>
    private static string Reason(Exception ex, CancellationToken token)
        => token.IsCancellationRequested ? TimeoutReason() : ex.Message;

    private JobStatus Snapshot(JobRecord j)
    {
        _ = SyncCommandRunAsync(j, null);
        return new JobStatus { Job = View(j), LogBytes = j.LogBytes };
    }

    private static JobStatus StaleStatus(JobRecord j, string reason)
        => new() { Job = View(j), LogBytes = j.LogBytes, RefreshError = reason };

    /// <summary>登录主机读取一个运行中任务的真实状态，并把变化写回数据库（Go probe）。</summary>
    private async Task<JobStatus> ProbeAsync(IRemoteConnection client, JobRecord j, CancellationToken ct)
    {
        var script = "d=\"$HOME/.onessh/jobs/" + j.Id + "\"; " + JobProcessIdentityScript(j) + "\n" +
            "if [ -f \"$d/exit\" ]; then printf 'exited:'; tr -d '\\n' < \"$d/exit\"; printf ':'; wc -c < \"$d/out.log\";\n" +
            "elif alive && owned; then printf 'running::'; wc -c < \"$d/out.log\";\n" +
            "else printf 'lost::'; wc -c 2>/dev/null < \"$d/out.log\" || echo 0; fi";
        var res = await _exec.RunAsync(client, script, "~", null,
            new ExecOptions { Timeout = TimeSpan.FromSeconds(15), MaxLines = 5 }, ct).ConfigureAwait(false);
        var parts = res.Output.Trim().Split(':');
        if (parts.Length < 3)
        {
            throw new SubSshException($"无法解析任务状态: \"{res.Output}\"");
        }
        var status = parts[0];
        long? code = null;
        if (status == "exited"
            && long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCode))
        {
            code = parsedCode;
        }
        long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
        j.LogBytes = size;
        _ = _store.RecordJobCheckAsync(j.Id, size, DateTimeOffset.Now.ToUnixTimeSeconds(), CancellationToken.None);
        _ = _store.UpdateCommandRunJobBytesAsync(j.Id, size, CancellationToken.None);
        if (status != j.Status)
        {
            _ = _store.UpdateJobStateAsync(j.Id, status, code, CancellationToken.None);
            j.Status = status;
            if (code != null)
            {
                j.ExitCode = code;
            }
            if (status != "running")
            {
                j.FinishedAt = DateTimeOffset.Now.ToUnixTimeSeconds();
            }
            Events.Publish(new JobStatusEvent(j.Id, status));
            _ = SyncCommandRunAsync(j, (int?)code);
        }
        return new JobStatus { Job = View(j), LogBytes = size };
    }

    private static JobView View(JobRecord j) => new()
    {
        Id = j.Id,
        HostId = j.HostId,
        Command = j.Command,
        Cwd = j.Cwd,
        Pid = j.Pid,
        UsedSetsid = j.UsedSetsid,
        Status = j.Status,
        ExitCode = j.ExitCode,
        StartedAt = j.StartedAt,
        FinishedAt = j.FinishedAt,
    };

    // ------------------------------------------------------------------
    // 日志（Go Logs / LogChunk）
    // ------------------------------------------------------------------

    public async Task<string> LogsAsync(JobRecord job, int tailLines, string pattern, long offset, CancellationToken ct = default)
    {
        var h = await _store.GetHostAsync(job.HostId, ct).ConfigureAwait(false)
            ?? throw new SubSshException($"主机不存在: {job.HostId}");
        var client = await _pool.GetAsync(h.Name, ct).ConfigureAwait(false);
        if (tailLines <= 0) tailLines = 100;
        if (tailLines > 5000) tailLines = 5000;
        var prefix = "d=\"$HOME/.onessh/jobs/" + job.Id + "\"; ";
        string cmd;
        if (offset > 0)
        {
            cmd = prefix + "tail -c +" + offset.ToString(CultureInfo.InvariantCulture) + " \"$d/out.log\"";
        }
        else if (pattern.Length != 0)
        {
            cmd = prefix + "grep -E " + Shq(pattern) + " \"$d/out.log\" | tail -n "
                + tailLines.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            cmd = prefix + "tail -n " + tailLines.ToString(CultureInfo.InvariantCulture) + " \"$d/out.log\"";
        }
        var res = await _exec.RunAsync(client, cmd, "~", null,
            new ExecOptions { Timeout = TimeSpan.FromSeconds(30), MaxLines = tailLines, Tail = true }, ct)
            .ConfigureAwait(false);
        return res.Output;
    }

    /// <summary>字节游标增量读取后台任务的合并日志。单次上限 128 KiB，既低于 Runner 的捕获上限，
    /// 也避免一个详情请求占用过多内存（Go LogChunk）。</summary>
    public async Task<JobLogChunk> LogChunkAsync(JobRecord job, long offset, int limit, CancellationToken ct = default)
    {
        var host = await _store.GetHostAsync(job.HostId, ct).ConfigureAwait(false)
            ?? throw new SubSshException($"主机不存在: {job.HostId}");
        var client = await _pool.GetAsync(host.Name, ct).ConfigureAwait(false);
        if (offset < 0) offset = 0;
        if (limit <= 0) limit = 128 << 10;
        if (limit > 128 << 10) limit = 128 << 10;
        var start = offset + 1;
        var readLimit = limit + 3;
        var command = "d=\"$HOME/.onessh/jobs/" + job.Id + "\"; f=\"$d/out.log\"; test -f \"$f\" || exit 44; "
            + "size=$(wc -c < \"$f\") || exit; printf '%s\\n' \"$size\"; "
            + "if [ \"$size\" -gt " + offset.ToString(CultureInfo.InvariantCulture) + " ]; then "
            + "count=$((size-" + offset.ToString(CultureInfo.InvariantCulture) + ")); "
            + "if [ \"$count\" -gt " + readLimit.ToString(CultureInfo.InvariantCulture) + " ]; then count="
            + readLimit.ToString(CultureInfo.InvariantCulture) + "; fi; "
            + "tail -c +" + start.ToString(CultureInfo.InvariantCulture) + " \"$f\" | head -c \"$count\"; fi";
        var result = await _exec.RunAsync(client, command, "~", null,
            new ExecOptions { Timeout = TimeSpan.FromSeconds(30), MaxLines = 10000 }, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new SubSshException("读取任务日志失败: " + result.Output.Trim());
        }
        var stdout = Encoding.UTF8.GetBytes(result.Stdout);
        var lineEnd = Array.IndexOf(stdout, (byte)'\n');
        if (lineEnd < 0)
        {
            throw new SubSshException("无法解析任务日志大小");
        }
        if (!long.TryParse(Encoding.UTF8.GetString(stdout, 0, lineEnd).Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
        {
            throw new SubSshException("无法解析任务日志大小");
        }
        var raw = stdout[(lineEnd + 1)..];
        var paged = Utf8Page(raw, limit, offset + (stdout.Length - lineEnd - 1) < total);
        var next = offset + paged.Length;
        _ = _store.UpdateJobLogBytesAsync(job.Id, total, CancellationToken.None);
        _ = _store.UpdateCommandRunJobBytesAsync(job.Id, total, CancellationToken.None);
        return new JobLogChunk
        {
            Content = Encoding.UTF8.GetString(paged), // 非法 UTF-8 由解码器替换为 U+FFFD（TODO: 与 Go ToValidUTF8 对齐）
            Offset = offset,
            NextOffset = next,
            TotalBytes = total,
            Complete = next >= total,
        };
    }

    // ------------------------------------------------------------------
    // 终止（Go Kill）：TERM→KILL 阶梯由远端脚本完成；状态写回 "killed"。
    // ------------------------------------------------------------------

    public async Task KillAsync(JobRecord job, string signal, CancellationToken ct = default)
    {
        // 已结束任务必须先直接返回：不能再使用持久化 PID，避免 PID 复用后误伤无关进程。
        if (job.Status != "running")
        {
            return;
        }
        if (signal != "TERM" && signal != "KILL")
        {
            throw new SubSshException("signal 仅支持 TERM 或 KILL");
        }
        var h = await _store.GetHostAsync(job.HostId, ct).ConfigureAwait(false)
            ?? throw new SubSshException($"主机不存在: {job.HostId}");
        var client = await _pool.GetAsync(h.Name, ct).ConfigureAwait(false);
        // 发信号前再次在远端核验 job 身份；setsid leader 消失时，仍可通过组员继承的环境标记
        // 验证 PGID。信号成功后等待整个目标退出，再读取最终日志大小。
        var script = "d=\"$HOME/.onessh/jobs/" + job.Id + "\"; " + JobProcessIdentityScript(job) + "\n" +
            "bytes() { wc -c 2>/dev/null < \"$d/out.log\" || printf '0\\n'; }\n" +
            "exited() { printf 'exited:'; tr -d '\\n' < \"$d/exit\"; printf ':'; bytes; }\n" +
            "signal_target() { kill -" + signal + " \"$target\" 2>/dev/null; }\n" +
            "if [ -f \"$d/exit\" ]; then\n" +
            "  exited\n" +
            "elif ! alive || ! owned; then\n" +
            "  printf 'lost::'; bytes\n" +
            "elif ! signal_target; then\n" +
            "  if [ -f \"$d/exit\" ]; then exited\n" +
            "  elif ! alive || ! owned; then printf 'lost::'; bytes\n" +
            "  else printf 'signal_failed::'; bytes; exit 45\n" +
            "  fi\n" +
            "else\n" +
            "  i=0\n" +
            "  while alive && [ \"$i\" -lt 30 ]; do\n" +
            "    sleep 0.1\n" +
            "    i=$((i+1))\n" +
            "  done\n" +
            "  if alive; then\n" +
            "    printf 'running::'; bytes\n" +
            "    exit 46\n" +
            "  fi\n" +
            "  printf 'killed::'; bytes\n" +
            "fi";
        var res = await _exec.RunAsync(client, script, "~", null,
            new ExecOptions { Timeout = TimeSpan.FromSeconds(6), MaxLines = 5 }, ct).ConfigureAwait(false);
        var parts = res.Output.Trim().Split(':', 3);
        if (res.ExitCode != 0)
        {
            throw new SubSshException("kill 失败: " + res.Output.Trim());
        }
        if (parts.Length != 3)
        {
            throw new SubSshException($"无法解析任务终止结果: \"{res.Output}\"");
        }
        var status = parts[0];
        if (status != "exited" && status != "killed" && status != "lost")
        {
            throw new SubSshException($"未知任务终止状态: \"{status}\"");
        }
        if (!long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
        {
            throw new SubSshException("无法解析任务日志大小");
        }
        long? code = null;
        if (status == "exited")
        {
            if (!long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                throw new SubSshException("无法解析任务退出码");
            }
            code = value;
            job.ExitCode = code;
        }
        else
        {
            job.ExitCode = null;
        }
        var persistCtx = CancellationToken.None;
        await _store.UpdateJobLogBytesAsync(job.Id, size, persistCtx).ConfigureAwait(false);
        await _store.UpdateCommandRunJobBytesAsync(job.Id, size, persistCtx).ConfigureAwait(false);
        await _store.UpdateJobStateAsync(job.Id, status, code, persistCtx).ConfigureAwait(false);
        job.Status = status;
        job.LogBytes = size;
        job.FinishedAt = DateTimeOffset.Now.ToUnixTimeSeconds();
        _ = SyncCommandRunAsync(job, (int?)code);
        Events.Publish(new JobStatusEvent(job.Id, status));
    }

    /// <summary>把后台任务状态同步到关联的命令记录并发布 command_finished（Go syncCommandRun）。</summary>
    private async Task SyncCommandRunAsync(JobRecord j, long? exitCode)
    {
        try
        {
            var run = await _store.GetCommandRunByJobAsync(j.Id, CancellationToken.None).ConfigureAwait(false);
            if (run == null || run.Status != "running")
            {
                return;
            }
            string status;
            var code = exitCode;
            switch (j.Status)
            {
                case "exited":
                    status = "failed";
                    if (code == null && j.ExitCode != null)
                    {
                        code = j.ExitCode;
                    }
                    if (code == 0)
                    {
                        status = "succeeded";
                    }
                    exitCode = code;
                    break;
                case "killed":
                    status = "cancelled";
                    break;
                case "lost":
                    status = "lost";
                    break;
                default:
                    return;
            }
            var finishedAt = j.FinishedAt != null ? j.FinishedAt.Value * 1000 : DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var changed = await _store.FinishCommandRunByJobAsync(j.Id, status, code, finishedAt, CancellationToken.None)
                .ConfigureAwait(false);
            if (!changed)
            {
                return;
            }
            Events.Publish(new CommandFinishedByJobEvent(
                run.Id, run.Tool, run.Host, status, code,
                j.LogBytes, 0, true, finishedAt));
        }
        catch
        {
            // 命令记录同步失败不影响任务状态本身（对应 Go 忽略 store 错误）。
        }
    }

    // ------------------------------------------------------------------
    // 维护（maintenance.go）：过期任务清理 + 命令记录同步。
    // ------------------------------------------------------------------

    public async Task StartMaintenanceAsync(CancellationToken ct = default)
    {
        lock (_maintenanceGate)
        {
            if (_maintenanceCts != null) return;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _maintenanceCts = cts;
            _ = MaintenanceLoopAsync(cts.Token);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task StopMaintenanceAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? cts;
        lock (_maintenanceGate)
        {
            cts = _maintenanceCts;
            _maintenanceCts = null;
        }
        if (cts != null)
        {
            cts.Cancel();
            cts.Dispose();
        }
        return Task.CompletedTask;
    }

    /// <summary>后台维护循环：首轮在 MaintenanceFirstDelay 后执行，之后每小时一轮。</summary>
    private async Task MaintenanceLoopAsync(CancellationToken token)
    {
        try
        {
            var first = true;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(first ? MaintenanceFirstDelay : MaintenanceInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                first = false;
                try
                {
                    var report = await MaintainAsync(DateTimeOffset.Now, token).ConfigureAwait(false);
                    if (report.Expired > 0 || report.Pruned > 0)
                    {
                        Console.Error.WriteLine(
                            $"后台任务维护: {report.Expired} 个长期无法确认的任务标为失联，清理 {report.Pruned} 条过期记录");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"后台任务维护失败: {ex.Message}");
                }
            }
        }
        finally
        {
            lock (_maintenanceGate)
            {
                if (ReferenceEquals(_maintenanceCts, null) == false && _maintenanceCts.IsCancellationRequested)
                {
                    _maintenanceCts = null;
                }
            }
        }
    }

    /// <summary>
    /// 执行一轮维护：
    /// 1. 逐台主机探测全部运行中任务。没人打开任务页时确认时间也要前进，否则健康的长任务会被误判失联；
    /// 2. 本轮确实探测过却没能确认、且连续 UnconfirmedLostAfter 未确认的任务标为 lost，并同步命令记录；
    /// 3. 删除结束超过 FinishedRetention 的任务记录。
    /// 只有「探测过并失败」的任务才可能被标 lost：没轮到探测（维护被取消）不构成失联证据。
    /// </summary>
    public async Task<MaintenanceReport> MaintainAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var report = new MaintenanceReport();
        var errors = new List<string>();
        try
        {
            var running = await _store.ListRunningJobsOnActiveHostsAsync(ct).ConfigureAwait(false);
            var failed = await ProbeRunningAsync(running, ct).ConfigureAwait(false);
            if (failed.Count > 0 && !ct.IsCancellationRequested)
            {
                var expired = await _store.ExpireUncheckedJobsAsync(
                    failed, now.Add(-UnconfirmedLostAfter).ToUnixTimeSeconds(), now.ToUnixTimeSeconds(), ct)
                    .ConfigureAwait(false);
                foreach (var j in expired)
                {
                    Events.Publish(new JobStatusEvent(j.Id, j.Status));
                    await SyncCommandRunAsync(j, null).ConfigureAwait(false);
                }
                report = report with { Expired = expired.Count };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }
        try
        {
            var pruned = await _store.PruneFinishedJobsAsync(
                now.Add(-FinishedRetention).ToUnixTimeSeconds(), ct).ConfigureAwait(false);
            report = report with { Pruned = pruned };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }
        if (errors.Count != 0)
        {
            throw new SubSshException(string.Join("; ", errors));
        }
        return report;
    }

    /// <summary>让每台主机都轮到一次探测，返回本轮探测失败的任务 ID（Go probeRunning）。
    /// 并发槽只在父取消时放弃等待，因此慢主机只会拖长这一轮，不会挤掉排在后面的主机。</summary>
    private async Task<List<string>> ProbeRunningAsync(IReadOnlyList<JobRecord> running, CancellationToken ct)
    {
        var byHost = new Dictionary<long, List<JobRecord>>();
        foreach (var j in running)
        {
            if (!byHost.TryGetValue(j.HostId, out var list))
            {
                byHost[j.HostId] = list = new List<JobRecord>();
            }
            list.Add(j);
        }
        var tasks = new List<Task<List<string>>>(byHost.Count);
        foreach (var (hostId, jobs) in byHost)
        {
            tasks.Add(ProbeHostAsync(ct, hostId, jobs));
        }
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var failed = new List<string>();
        foreach (var r in results)
        {
            failed.AddRange(r);
        }
        return failed;
    }

    /// <summary>在独立预算内探测一台主机，返回失败的任务 ID；没轮到或维护被取消时返回空
    /// （Go probeHost）。预算到点而 worker 仍挂着同样算探测失败，并登记为挂起，
    /// 列表刷新与下一轮维护都不会再为这台主机叠加新的 worker。</summary>
    private async Task<List<string>> ProbeHostAsync(CancellationToken parentCt, long hostId, IReadOnlyList<JobRecord> jobs)
    {
        List<string> Ids() => jobs.Select(j => j.Id).ToList();
        if (HostStuck(hostId))
        {
            // 之前登录这台主机的请求至今没有回应，本身就是无法确认的证据
            return Ids();
        }
        try
        {
            await _maintenanceSem.WaitAsync(parentCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        try
        {
            using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
            hostCts.CancelAfter(MaintenanceHostBudget);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = Task.Run(() => RefreshHostAsync(hostCts.Token, hostId, jobs), CancellationToken.None);
            _ = worker.ContinueWith(_ => done.TrySetResult(), TaskScheduler.Default);
            try
            {
                var statuses = await worker.WaitAsync(hostCts.Token).ConfigureAwait(false);
                if (parentCt.IsCancellationRequested)
                {
                    return [];
                }
                var failed = new List<string>();
                for (var i = 0; i < statuses.Length; i++)
                {
                    if (statuses[i].RefreshError != null) failed.Add(jobs[i].Id);
                }
                return failed;
            }
            catch (OperationCanceledException)
            {
                if (parentCt.IsCancellationRequested)
                {
                    return [];
                }
                _stuck[hostId] = done;
                return Ids();
            }
        }
        finally
        {
            _maintenanceSem.Release();
        }
    }

    // ------------------------------------------------------------------
    // UTF-8 分页（与 Go execx.UTF8Page 同语义，在字符边界截断；
    // 被保留的尾部字节由下一页从相同偏移重读）。
    // ------------------------------------------------------------------

    private static byte[] Utf8Page(byte[] data, int limit, bool hasMore)
    {
        if (limit <= 0 || data.Length <= limit)
        {
            return CompleteUtf8Prefix(data, hasMore);
        }
        var page = CompleteUtf8Prefix(data[..limit], hasMore: true);
        if (page.Length > 0) return page;
        var size = Math.Clamp(Utf8RuneSize(data), 1, data.Length);
        return data[..size];
    }

    private static byte[] CompleteUtf8Prefix(byte[] data, bool hasMore)
    {
        if (!hasMore || data.Length == 0) return data;
        var start = data.Length - 1;
        while (start > 0 && !RuneStart(data[start])) start--;
        return Utf8FullRune(data, start) ? data : data[..start];
    }

    private static bool RuneStart(byte b) => (b & 0xC0) != 0x80;

    private static int Utf8RuneSize(byte[] data)
    {
        if (data.Length == 0) return 0;
        var b0 = data[0];
        if (b0 < 0x80) return 1;
        if ((b0 & 0xE0) == 0xC0) return 2;
        if ((b0 & 0xF0) == 0xE0) return 3;
        if ((b0 & 0xF8) == 0xF0) return 4;
        return 0;
    }

    private static bool Utf8FullRune(byte[] data, int offset)
    {
        var remaining = data.Length - offset;
        if (remaining <= 0) return false;
        var size = Utf8RuneSize(data.AsSpan(offset).ToArray());
        if (size <= 0 || size > remaining) return false;
        if (size == 1) return true;
        for (var i = 1; i < size; i++)
        {
            if (!RuneStart(data[offset + i])) return false;
        }
        return true;
    }
}
