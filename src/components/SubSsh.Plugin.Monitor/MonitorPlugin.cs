// SubSsh.Plugin.Monitor — 公共主机监控组件插件（可插拔、可任意替换：换同接口插件即可替换监控实现）。
// GPL-3.0，移植自 OneSSH internal/monitor/monitor.go（采样命令与 /proc 解析逻辑逐行移植）。
// 时空可组合：IConnectionPool + IOneSshStoreFull + ICommandRunner + ISettings 四个依赖全部在线时
// 构建并发布 IHostMonitor；任一下线即静默撤回，等待依赖回归。
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;

namespace SubSsh.Plugin.Monitor;

[Plugin("monitor", "公共监控组件：monitor_enabled 主机 CPU/mem/load/disks 定时轮询采样 + metrics 落库（可被同接口插件替换）")]
public sealed class MonitorPlugin : IComponent
{
    public string Name => "monitor";

    private ISettings? _settings;
    private IOneSshStoreFull? _store;
    private IConnectionPool? _pool;
    private ICommandRunner? _runner;
    private HostMonitor? _monitor;
    private readonly IContext?[] _ctxSlot = [null!]; // StartAsync 期间固定的 ctx 引用

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        _ctxSlot[0] = ctx;

        ctx.Require<ISettings>(s => { _settings = s; TryBuild(ctx); },
            () => { _settings = null; TryUnbuild(ctx); });
        ctx.Require<IOneSshStoreFull>(s => { _store = s; TryBuild(ctx); },
            () => { _store = null; TryUnbuild(ctx); });
        ctx.Require<IConnectionPool>(s => { _pool = s; TryBuild(ctx); },
            () => { _pool = null; TryUnbuild(ctx); });
        ctx.Require<ICommandRunner>(s => { _runner = s; TryBuild(ctx); },
            () => { _runner = null; TryUnbuild(ctx); });

        return Task.CompletedTask;
    }

    private void TryBuild(IContext ctx)
    {
        if (_monitor != null || _settings == null || _store == null || _pool == null || _runner == null) return;

        // 轮询间隔来自 ISettings.PollInterval（秒；<=0 表示不自动轮询，与 Go 版 interval<=0 一致）
        var monitor = new HostMonitor(_store, _pool, _runner, TimeSpan.FromSeconds(_settings.PollInterval));
        _monitor = monitor;
        // 时间可组合：启动/停止登记为可撤销效果，卸载 LIFO 自动回滚
        ctx.Track(
            apply: () => monitor.StartAsync().GetAwaiter().GetResult(),
            revert: () => monitor.StopAsync().GetAwaiter().GetResult());
        ctx.Provide<IHostMonitor>(monitor);
    }

    private void TryUnbuild(IContext ctx)
    {
        if (_monitor == null) return;
        ctx.Unprovide<IHostMonitor>();
        _monitor = null; // Track 登记的 revert（StopAsync）由卸载管线按 LIFO 执行
    }
}

// 实现位于 HostMonitor.cs / MonitoringParser.cs。
