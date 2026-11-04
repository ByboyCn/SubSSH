// SubSsh.Plugin.Jobs — 后台任务组件插件（可插拔、可任意替换）。
// 全量移植 OneSSH internal/jobs/manager.go + maintenance.go：
// Start/StartTracked（setsid+nohup 落盘 PID 与日志，远端 argv 标记身份核验）、
// Refresh/RefreshAll（按主机并发探测、整批 8 秒硬预算、挂住主机登记 stuck）、
// Logs/LogChunk（tail/grep 或字节游标增量，UTF-8 字符边界截断）、
// Kill（TERM/KILL 白名单 + "job_killed" 语义的 killed 状态）、
// StartMaintenance/StopMaintenance（首轮 1 分钟后、之后每小时一轮：长期无法确认的
// 运行任务标 lost、删除超过保留期的已结束记录，文案与 Go 版一致）。
// 依赖：Require IOneSshStoreFull（任务/命令记录存取）、ICommandRunner（远端探测脚本执行）、
// IConnectionPool（对应 Go jobs.Manager 持有的 sshpool.Pool）。
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;
using SubSsh.Core.Events;

namespace SubSsh.Plugin.Jobs;

[Plugin("jobs", "后台任务组件：setsid 启动/状态刷新/日志增量/终止/维护清理（可被同接口插件替换）")]
public sealed class JobsPlugin : IComponent
{
    public string Name => "jobs";

    private JobManagerService? _svc;
    private IWebApiEventBus? _sse;
    private Action? _sseUnwire;

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        // SSE 桥接（可选）：WebApi 在位时把 job_status/command_job_linked/command_finished 转发进 /api/events
        ctx.Require<IWebApiEventBus>(bus => { _sse = bus; WireSse(); },
            () => { _sse = null; UnwireSse(); });
        ctx.Require<IOneSshStoreFull>(
            store =>
            {
                ctx.Require<ICommandRunner>(
                    exec =>
                    {
                        ctx.Require<IConnectionPool>(
                            pool =>
                            {
                                if (_svc != null)
                                {
                                    _svc.AttachStore(store);
                                    ctx.Provide<IJobManager>(_svc);
                                    return;
                                }
                                _svc = new JobManagerService(pool, exec);
                                _svc.AttachStore(store);
                                WireSse(); // 服务晚于 SSE 桥上线时补挂
                                ctx.Provide<IJobManager>(_svc);
                            },
                            () => ctx.Unprovide<IJobManager>());
                    },
                    () => ctx.Unprovide<IJobManager>());
            },
            () => ctx.Unprovide<IJobManager>());
        return Task.CompletedTask;
    }

    private void WireSse()
    {
        var bus = _sse;
        var svc = _svc;
        if (bus == null || svc == null || _sseUnwire != null) return;
        Action<JobStatusEvent> onJob = e => bus.Publish("job_status", e);
        Action<CommandJobLinkedEvent> onLinked = e => bus.Publish("command_job_linked", e);
        Action<CommandFinishedByJobEvent> onFinished = e => bus.Publish("command_finished", e);
        svc.Events.Subscribe(onJob);
        svc.Events.Subscribe(onLinked);
        svc.Events.Subscribe(onFinished);
        _sseUnwire = () =>
        {
            svc.Events.Unsubscribe(onJob);
            svc.Events.Unsubscribe(onLinked);
            svc.Events.Unsubscribe(onFinished);
        };
    }

    private void UnwireSse()
    {
        _sseUnwire?.Invoke();
        _sseUnwire = null;
    }
}

// ---------------------------------------------------------------------------
// 事件载荷（对应 Go events.Publish 的 map，字段名与原版一致）。
// ---------------------------------------------------------------------------

/// <summary>对应 Go events.Publish("job_status", {"job_id", "status"})。</summary>
public sealed record JobStatusEvent(string JobId, string Status);

/// <summary>对应 Go events.Publish("command_job_linked", {"run_id", "job_id"})。</summary>
public sealed record CommandJobLinkedEvent(string RunId, string JobId);

/// <summary>对应 Go syncCommandRun 里的 events.Publish("command_finished", map)。</summary>
public sealed record CommandFinishedByJobEvent(
    string RunId, string Tool, string Host, string Status, long? ExitCode,
    long StdoutBytes, long StderrBytes, bool OutputAvailable, long FinishedAt);

/// <summary>维护一轮的结果（对应 Go jobs.MaintenanceReport）。</summary>
public sealed record MaintenanceReport
{
    /// <summary>因长期无法确认被标为 lost 的运行中任务。</summary>
    public int Expired { get; init; }
    /// <summary>超过保留期被删除的已结束任务。</summary>
    public long Pruned { get; init; }
}
