// SubSsh.Plugin.McpCore — 公共 MCP 组件插件（可插拔、可任意替换）。
// GPL-3.0，移植自 SubSSH internal/mcpserver/server.go（手写 JSON-RPC 2.0，不用 SDK）。
// 时空可组合：ISettings + ISubSshStoreFull + IToolGroups 三个依赖全部在线时构建并发布
// IMcpServer / IToolRegistry / McpHttpMount；任一下线即静默撤回，等待依赖回归。
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;

namespace SubSsh.Plugin.McpCore;

[Plugin("mcp-core", "公共 MCP 组件：手写 JSON-RPC 2.0 MCP 服务器（token 认证、disabled_tools、command_runs 审计、fanout；可被同接口插件替换）")]
public sealed class McpCorePlugin : IComponent
{
    public string Name => "mcp-core";

    private ISettings? _settings;
    private ISubSshStoreFull? _store;
    private IToolGroups? _groups;
    private IConnectionPool? _pool;
    private ICommandRunner? _runner;
    private IWebApiEventBus? _sse;
    private Action? _sseUnwire;
    private McpServerCore? _server;
    private readonly IContext?[] _ctxSlot = [null!]; // StartAsync 期间固定的 ctx 引用

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        _ctxSlot[0] = ctx;

        ctx.Require<ISettings>(s => { _settings = s; TryBuild(ctx); },
            () => { _settings = null; TryUnbuild(ctx); });
        ctx.Require<ISubSshStoreFull>(s => { _store = s; TryBuild(ctx); },
            () => { _store = null; TryUnbuild(ctx); });
        ctx.Require<IToolGroups>(s => { _groups = s; TryBuild(ctx); },
            () => { _groups = null; TryUnbuild(ctx); });
        // fanout（exec_many）需要连接池与执行器；缺任一时 exec_many 返回组件缺失错误（见 TODO）
        ctx.Require<IConnectionPool>(s => { _pool = s; TryBuild(ctx); },
            () => { _pool = null; TryUnbuild(ctx); });
        ctx.Require<ICommandRunner>(s => { _runner = s; TryBuild(ctx); },
            () => { _runner = null; TryUnbuild(ctx); });
        // SSE 桥接（可选）：WebApi 在位时把 tool_call/command_started/command_finished 转发进 /api/events
        ctx.Require<IWebApiEventBus>(b => { _sse = b; WireSse(); },
            () => { _sse = null; UnwireSse(); });

        return Task.CompletedTask;
    }

    private void WireSse()
    {
        var bus = _sse;
        var server = _server;
        if (bus == null || server == null || _sseUnwire != null) return;
        Action<ToolCallEvent> onTool = e => bus.Publish("tool_call", e);
        Action<CommandStartedEvent> onStarted = e => bus.Publish("command_started", e);
        Action<CommandFinishedEvent> onFinished = e => bus.Publish("command_finished", e);
        server.Events.Subscribe(onTool);
        server.Events.Subscribe(onStarted);
        server.Events.Subscribe(onFinished);
        _sseUnwire = () =>
        {
            server.Events.Unsubscribe(onTool);
            server.Events.Unsubscribe(onStarted);
            server.Events.Unsubscribe(onFinished);
        };
    }

    private void UnwireSse()
    {
        _sseUnwire?.Invoke();
        _sseUnwire = null;
    }

    private void TryBuild(IContext ctx)
    {
        if (_server != null || _settings == null || _store == null || _groups == null) return;

        var server = new McpServerCore(_settings, _store, _groups, _pool, _runner);
        ctx.Track(
            apply: () => { },
            revert: () => server.DisposeAsync().AsTask());
        _server = server;
        WireSse(); // SSE 桥接先于服务器已就绪时补挂
        // 时间可组合：启动登记为可撤销效果，卸载 LIFO 自动回滚
        ctx.Track(
            apply: () => server.StartAsync().GetAwaiter().GetResult(),
            revert: () => server.StopAsync().GetAwaiter().GetResult());
        ctx.Provide<IToolRegistry>(server.Registry);
        ctx.Provide<IMcpServer>(server);
        ctx.Provide(server.HttpMount);
        ctx.Provide<IMcpHttpHandler>(server.HttpMount); // 传输插件（McpHttp）经 Core 抽象复用 handle 能力
    }

    private void TryUnbuild(IContext ctx)
    {
        if (_server == null) return;
        UnwireSse();
        ctx.Unprovide<IMcpHttpHandler>();
        ctx.Unprovide<McpHttpMount>();
        ctx.Unprovide<IMcpServer>();
        ctx.Unprovide<IToolRegistry>();
        _server = null; // Track 登记的 revert（Stop/Dispose）由卸载管线按 LIFO 执行
    }
}
