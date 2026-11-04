// SubSsh.Plugin.Monitor — HostMonitor：monitor.go Manager 的 C# 移植。
// Start/Stop：定时轮询 + 7 天清理 + WAL checkpoint；Poll：并发上限 5 的全量采样（去重）；
// Sample：/proc + df 解析与差分 CPU；Fresh：双 /proc 采样立即计算 CPU。
// 说明：Go 用单个 goroutine + select 分发三个 ticker，这里改为三个独立循环（语义等价：
// 各 ticker 触发互不阻塞），goroutine → Task、轮询去重 → Interlocked。
using System.Text.Json;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.Monitor;

public sealed class HostMonitor(
    ISubSshStoreFull store,
    IConnectionPool pool,
    ICommandRunner exec,
    TimeSpan interval) : IHostMonitor
{
    // Go: const command = `cat /proc/stat /proc/meminfo /proc/loadavg 2>/dev/null || uptime; echo __SUBSSH_DF__; df -P -k`
    private const string Command =
        "cat /proc/stat /proc/meminfo /proc/loadavg 2>/dev/null || uptime; echo __SUBSSH_DF__; df -P -k";

    // Go: const cmd = `cat /proc/stat /proc/meminfo /proc/loadavg 2>/dev/null; echo __SUBSSH_FIRST__; sleep 1; cat /proc/stat /proc/meminfo /proc/loadavg 2>/dev/null; echo __SUBSSH_DF__; df -P -k`
    private const string FreshCommand =
        "cat /proc/stat /proc/meminfo /proc/loadavg 2>/dev/null; echo __SUBSSH_FIRST__; sleep 1; " +
        "cat /proc/stat /proc/meminfo /proc/loadavg 2>/dev/null; echo __SUBSSH_DF__; df -P -k";

    // Go: maxConcurrentSamples = 5
    private const int MaxConcurrentSamples = 5;

    private const string DfMarker = "__SUBSSH_DF__";
    private const string FirstMarker = "__SUBSSH_FIRST__";

    private readonly TimeSpan _interval = interval;
    private readonly object _mu = new();
    // Go: previous map[int64]CPU —— 差分 CPU 用（hostID → 上一次 total/idle jiffies）
    private readonly Dictionary<long, (ulong Total, ulong Idle)> _previous = [];

    private CancellationTokenSource? _cts;

    // ---------------- Start / Stop（Go Manager.Start/Stop） ----------------

    public Task StartAsync(CancellationToken ct = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        _ = CleanupAsync(cts.Token); // Go Start 里先 cleanup 一次
        _ = CleanLoopAsync(cts.Token);       // hourly 清理 ticker
        _ = CheckpointLoopAsync(cts.Token);  // 10 分钟 WAL checkpoint ticker
        if (_interval > TimeSpan.Zero)
        {
            _ = PollAsync(cts.Token);        // Go: 启动即先 Poll 一次
            _ = PollLoopAsync(cts.Token);    // 轮询 ticker
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var poll = new PeriodicTimer(_interval);
        try
        {
            while (await poll.WaitForNextTickAsync(ct).ConfigureAwait(false))
                _ = PollAsync(ct);
        }
        catch (OperationCanceledException) { }
    }

    private async Task CleanLoopAsync(CancellationToken ct)
    {
        using var clean = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await clean.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await CleanupAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckpointLoopAsync(CancellationToken ct)
    {
        using var ckpt = new PeriodicTimer(TimeSpan.FromMinutes(10));
        try
        {
            while (await ckpt.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var (busy, _, _) = await store.CheckpointWalAsync(ct).ConfigureAwait(false);
                    if (busy) Console.Error.WriteLine("WAL checkpoint 被阻塞（busy=1），将在下次重试");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"WAL checkpoint 失败: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // 7 天清理（Go Manager.cleanup）
    private async Task CleanupAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds();
        try
        {
            await store.CleanupMetricsAsync(cutoff, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { Console.Error.WriteLine($"清理过期 metrics 失败: {ex.Message}"); }

        try
        {
            await exec.CleanupArtifactsAsync(DateTimeOffset.UtcNow.AddDays(-7), ct).ConfigureAwait(false);
        }
        catch (Exception ex) { Console.Error.WriteLine($"清理过期 artifact 失败: {ex.Message}"); }

        try
        {
            await store.ExpireCommandRunOutputsAsync(cutoff, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { Console.Error.WriteLine($"标记过期命令输出失败: {ex.Message}"); }

        try
        {
            var deletable = await store.DeletableCommandRunOutputIdsAsync(ct).ConfigureAwait(false);
            await exec.CleanupCommandOutputsAsync(deletable, ct).ConfigureAwait(false);
            await store.MarkCommandRunOutputsCleanedAsync(deletable, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { Console.Error.WriteLine($"清理过期命令输出失败: {ex.Message}"); }
    }

    // ---------------- Poll（Go Manager.Poll：并发上限 5、去重） ----------------

    private int _polling; // 0/1 去重标志（Go pollMu+polling）

    public async Task PollAsync(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return;
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        try
        {
            var hosts = await store.MonitoredHostsAsync(ct).ConfigureAwait(false);
            var workerCount = Math.Min(MaxConcurrentSamples, hosts.Count);
            if (workerCount == 0) return;

            using var gate = new SemaphoreSlim(workerCount);
            var jobs = hosts.Select(async h =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await SampleAsync(h, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"监控采样 {h.Name} 失败: {ex.Message}");
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(jobs).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"监控主机列表失败: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    // ---------------- Sample（Go Manager.Sample：差分 CPU） ----------------

    public async Task<MonitorSnapshot> SampleAsync(HostConfig host, CancellationToken ct = default)
    {
        var client = await pool.GetAsync(host.Name, ct).ConfigureAwait(false);
        var res = await exec.RunAsync(client, Command, "~", null,
            new ExecOptions { Timeout = TimeSpan.FromSeconds(30), MaxLines = 2000 }, ct).ConfigureAwait(false);
        if (!res.ExitCodeKnown || res.ExitCode != 0)
            throw new SubSshException($"采样命令退出 {res.ExitCode}");

        (ulong Total, ulong Idle)? prev;
        lock (_mu)
        {
            prev = _previous.TryGetValue(host.Id, out var p) ? p : null;
        }

        var snap = MonitoringParser.Parse(host.Id, res.Output, prev, out var cpu, out ulong? newTotal);

        lock (_mu)
        {
            if (newTotal is > 0)
                _previous[host.Id] = (newTotal.Value, cpu.Idle);
        }

        await store.AddMetricAsync(ToMetric(snap), ct).ConfigureAwait(false);
        return snap;
    }

    // ---------------- Fresh（Go Manager.Fresh：双采样立即算 CPU） ----------------

    public async Task<MonitorSnapshot> FreshAsync(HostConfig host, CancellationToken ct = default)
    {
        var client = await pool.GetAsync(host.Name, ct).ConfigureAwait(false);
        var res = await exec.RunAsync(client, FreshCommand, "~", null,
            new ExecOptions { Timeout = TimeSpan.FromSeconds(35), MaxLines = 3000 }, ct).ConfigureAwait(false);

        var idx = res.Output.IndexOf(FirstMarker, StringComparison.Ordinal);
        if (idx < 0)
            throw new SubSshException("主机不支持 /proc fresh 采样");
        var first = res.Output[..idx];
        var second = res.Output[(idx + FirstMarker.Length)..];

        var cpu1 = MonitoringParser.ParseCpu(first);
        var snap = MonitoringParser.Parse(host.Id, second, (cpu1.Total, cpu1.Idle), out _, out _);

        await store.AddMetricAsync(ToMetric(snap), ct).ConfigureAwait(false);
        return snap;
    }

    // ---------------- metric 落库 / 回读（Go toMetric / FromMetric） ----------------

    internal static MetricRecord ToMetric(MonitorSnapshot s) => new()
    {
        HostId = s.HostId,
        Ts = s.Ts,
        CpuPct = s.CpuPct,
        MemUsedKb = s.MemUsedKb,
        MemTotalKb = s.MemTotalKb,
        Load1 = s.Load1,
        DisksJson = JsonSerializer.Serialize(s.Disks),
    };

    internal static MonitorSnapshot FromMetric(MetricRecord m)
    {
        var disks = new List<MonitorDisk>();
        try
        {
            if (!string.IsNullOrEmpty(m.DisksJson))
                disks = JsonSerializer.Deserialize<List<MonitorDisk>>(m.DisksJson) ?? [];
        }
        catch (JsonException) { }

        return new MonitorSnapshot
        {
            HostId = m.HostId,
            Ts = m.Ts,
            CpuPct = m.CpuPct,
            MemUsedKb = m.MemUsedKb,
            MemTotalKb = m.MemTotalKb,
            Load1 = m.Load1,
            Disks = disks,
        };
    }
}
