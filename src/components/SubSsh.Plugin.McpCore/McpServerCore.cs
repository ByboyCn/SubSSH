// MCP 协议服务器（对应 mcpserver/server.go 的分发面，手写 JSON-RPC 2.0，不用 SDK）。
// 支持方法：initialize / notifications/* / ping / tools/list / tools/call；
// tools/call 经审计包装（audit 记录 + tool_call 事件），文案与 Go 原版一致。
using System.Text.Json;
using SubSsh.Core.Abstractions;
using SubSsh.Core.Events;
using SubSsh.Core.ToolGroups;

namespace SubSsh.Plugin.McpCore;

public sealed class McpServerCore : IMcpServer
{
    private readonly ISettings _settings;
    private readonly ISubSshStoreFull _store;
    private readonly IToolGroups _groups;
    private readonly IConnectionPool? _pool;
    private readonly ICommandRunner? _runner;
    private readonly EventBus _bus = new();
    private volatile bool _started;

    public McpServerCore(ISettings settings, ISubSshStoreFull store, IToolGroups groups,
        IConnectionPool? pool, ICommandRunner? runner)
    {
        _settings = settings;
        _store = store;
        _groups = groups;
        _pool = pool;
        _runner = runner;
        Registry = new ToolRegistry();
        HttpMount = new McpHttpMount(this, new BearerAuthenticator(store), ResolveDisabledAsync);
        RegisterBuiltinTools();
    }

    public ToolRegistry Registry { get; }
    public McpHttpMount HttpMount { get; }
    public string Name => "SubSSH";
    public string Version => "dev";
    public EventBus Events => _bus;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_started) return Task.CompletedTask;
        // 对应 Go New：恢复中断的命令执行记录
        _ = _store.RecoverInterruptedCommandRunsAsync(DateTimeOffset.Now.ToUnixTimeMilliseconds(), ct)
            .ContinueWith(t => { }, TaskScheduler.Default);
        _started = true;
        var names = InstanceDisabled().Names();
        if (names.Count > 0) Console.Error.WriteLine("已禁用 MCP 工具组: " + string.Join(",", names));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default) { _started = false; return Task.CompletedTask; }

    public ValueTask DisposeAsync() { _started = false; return ValueTask.CompletedTask; }

    public Task<string> GetInstructionsAsync(DisabledGroups disabled, CancellationToken ct = default) =>
        Task.FromResult(InstructionsText.Build(disabled));

    // ------------------------------------------------------------------
    // 禁用组：实例级（settings.DisabledTools）+ 令牌级（principal.DisabledTools）取并集；
    // 任一侧解析失败 fail-closed 回退 AllDisabled（对应 Go effectiveDisabled 语义）。
    // ------------------------------------------------------------------
    internal DisabledGroups InstanceDisabled()
    {
        try { return _groups.ParseListAsync(_settings.DisabledTools).GetAwaiter().GetResult(); }
        catch (Exception) { return _groups.AllDisabled(); }
    }

    internal async Task<DisabledGroups> ResolveDisabledAsync(McpCallContext? principal, CancellationToken ct)
    {
        var instance = InstanceDisabled();
        if (principal == null || principal.DisabledTools.Length == 0) return instance;
        DisabledGroups tokenLevel;
        try { tokenLevel = await _groups.ParseListAsync(principal.DisabledTools, ct).ConfigureAwait(false); }
        catch (Exception) { return _groups.AllDisabled(); }
        return DisabledGroups.Union([instance, tokenLevel]);
    }

    // ------------------------------------------------------------------
    // JSON-RPC 分发面
    // ------------------------------------------------------------------
    public async Task<string> HandleMessageAsync(string jsonRpcMessage, McpCallContext? principal, CancellationToken ct = default)
    {
        var msg = JsonRpcParser.TryParse(jsonRpcMessage);
        if (msg == null)
            return JsonRpcResponder.Error(new JsonRpcMessage { Id = JsonElement.Parse("null") },
                JsonRpcError.ParseError, "解析失败");
        try
        {
            return msg.Method switch
            {
                "initialize" => await HandleInitializeAsync(msg, principal, ct).ConfigureAwait(false),
                "ping" => JsonRpcResponder.Result(msg, new Dictionary<string, object?>()),
                "tools/list" => await HandleToolsListAsync(msg, principal, ct).ConfigureAwait(false),
                "tools/call" => await HandleToolsCallAsync(msg, principal, ct).ConfigureAwait(false),
                var m when m.StartsWith("notifications/", StringComparison.Ordinal) => "", // 通知不回包
                _ => JsonRpcResponder.Error(msg, JsonRpcError.MethodNotFound, "method not found: " + msg.Method),
            };
        }
        catch (SubSshException ex)
        {
            // 业务异常转 isError 结果（CONVENTIONS：MCP 层转 isError，文案与 Go 版一致）
            return JsonRpcResponder.Result(msg, ToolResultJson(McpToolResult.Error(ex.Message)));
        }
        catch (OperationCanceledException)
        {
            return JsonRpcResponder.Error(msg, JsonRpcError.InternalError, "请求已取消");
        }
        catch (Exception ex)
        {
            return JsonRpcResponder.Error(msg, JsonRpcError.InternalError, ex.Message);
        }
    }

    private async Task<string> HandleInitializeAsync(JsonRpcMessage msg, McpCallContext? principal, CancellationToken ct)
    {
        var disabled = await ResolveDisabledAsync(principal, ct).ConfigureAwait(false);
        var serverInfo = new Dictionary<string, object?> { ["name"] = Name, ["version"] = Version };
        if (_settings.PublicUrl.Length > 0)
        {
            // 图标走 spec 2025-11-25 起的 Implementation.icons；选 PNG 是为互操作性；
            // 未配置 PublicUrl 时宁可不发图标，也不发客户端解析不了的相对路径。
            serverInfo["icons"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["source"] = _settings.PublicUrl + "/logo.png",
                    ["mimeType"] = "image/png",
                    ["sizes"] = new[] { "256x256" },
                },
            };
        }
        var result = new Dictionary<string, object?>
        {
            ["protocolVersion"] = ProtocolVersionOf(msg.Params),
            ["capabilities"] = new Dictionary<string, object?> { ["tools"] = new Dictionary<string, object?>() },
            ["serverInfo"] = serverInfo,
            ["instructions"] = InstructionsText.Build(disabled),
        };
        return JsonRpcResponder.Result(msg, result);
    }

    // 服务端支持的协议版本（MCP 规范版本协商：请求版本不在集合内时返回最新支持版本，
    // 客户端据此判断兼容性——无条件回显未知版本会导致客户端断开并无限重连）
    private static readonly string[] SupportedProtocolVersions =
        ["2025-06-18", "2025-03-26", "2024-11-05"];

    private static string ProtocolVersionOf(JsonElement @params)
    {
        var requested = @params.ValueKind == JsonValueKind.Object
            && @params.TryGetProperty("protocolVersion", out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        return requested != null && SupportedProtocolVersions.Contains(requested)
            ? requested
            : SupportedProtocolVersions[0]; // 最新支持版本
    }

    private async Task<string> HandleToolsListAsync(JsonRpcMessage msg, McpCallContext? principal, CancellationToken ct)
    {
        var disabled = await ResolveDisabledAsync(principal, ct).ConfigureAwait(false);
        var tools = await Registry.ListAsync(disabled, ct).ConfigureAwait(false);
        var result = new Dictionary<string, object?>
        {
            ["tools"] = tools.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = t.InputSchema.Clone(),
            }).ToList(),
        };
        return JsonRpcResponder.Result(msg, result);
    }

    private async Task<string> HandleToolsCallAsync(JsonRpcMessage msg, McpCallContext? principal, CancellationToken ct)
    {
        if (msg.Params.ValueKind != JsonValueKind.Object)
            return JsonRpcResponder.Error(msg, JsonRpcError.InvalidParams, "params 必须为对象");
        var name = msg.Params.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? "" : "";
        var args = msg.Params.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a.Clone() : JsonSerializer.SerializeToElement(new Dictionary<string, object?>(), McpJson.Options);
        if (name.Length == 0)
            return JsonRpcResponder.Error(msg, JsonRpcError.InvalidParams, "缺少工具名 name");

        var disabled = await ResolveDisabledAsync(principal, ct).ConfigureAwait(false);
        if (disabled.HidesTool(name))
        {
            // 对应 Go installTokenDenylist：拒绝 + 审计
            var deniedResult = McpToolResult.Error("tool not allowed: " + name);
            await _store.AddAuditAsync(new AuditEntry
            {
                Ts = Now(), Tool = name, ParamsJson = Audit.RedactArgs(args), Ok = false,
                TokenId = principal is { TokenId: > 0 } ? principal.TokenId : null,
                TokenName = principal is { TokenId: > 0 } ? principal.TokenName : null,
                DurationMs = 0,
            }).ConfigureAwait(false);
            _bus.Publish(new ToolCallEvent(name, null, Ok: false, DurationMs: 0, Summary: null));
            return JsonRpcResponder.Result(msg, ToolResultJson(deniedResult));
        }

        if (!Registry.TryGet(name, out var tool))
            return JsonRpcResponder.Error(msg, JsonRpcError.InvalidParams, "unknown tool: " + name);

        // 审计包装（对应 Go register 的 handler 包装：计时、脱敏参数、host、run_ids、bytes_out）
        var started = DateTimeOffset.Now;
        McpToolResult result;
        try
        {
            result = await tool.Handler(args, principal ?? McpCallContext.Empty, ct)
                .ConfigureAwait(false);
        }
        catch (SubSshException ex)
        {
            result = McpToolResult.Error(ex.Message);
        }
        var ok = !result.IsError;
        var extras = AuditExtras.Peek;
        if (ok && extras != null && extras.ExitCodeSet) ok = extras.ExitCode == 0;
        var duration = (long)(DateTimeOffset.Now - started).TotalMilliseconds;
        var bytesOut = JsonSerializer.Serialize(result.Texts, McpJson.Options).Length;
        await _store.AddAuditAsync(new AuditEntry
        {
            Ts = Now(), Tool = name, ParamsJson = Audit.RedactArgs(args), Ok = ok,
            TokenId = principal is { TokenId: > 0 } ? principal.TokenId : null,
            TokenName = principal is { TokenId: > 0 } ? principal.TokenName : null,
            Host = Audit.HostOf(args), RunIds = extras?.RunIds.Count > 0 ? [.. extras.RunIds] : null,
            ExitCode = extras?.ExitCodeSet == true ? extras.ExitCode : null,
            DurationMs = duration, BytesOut = bytesOut,
        }).ConfigureAwait(false);
        _bus.Publish(new ToolCallEvent(name, Audit.HostOf(args), ok, duration, Audit.CallSummary(args)));
        return JsonRpcResponder.Result(msg, ToolResultJson(result));
    }

    private static Dictionary<string, object?> ToolResultJson(McpToolResult r) => new()
    {
        ["content"] = r.Texts.Select(t => new Dictionary<string, object?>
        {
            ["type"] = "text",
            ["text"] = t,
        }).ToList(),
        ["isError"] = r.IsError,
    };

    private static long Now() => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    // ------------------------------------------------------------------
    // 内置工具：fanout exec_many（对应 mcpserver/fanout.go；其余工具组由 Plugin.Tools.* 注册）
    // ------------------------------------------------------------------
    private void RegisterBuiltinTools()
    {
        if (_pool == null || _runner == null) return; // TODO：依赖后上线时补注册 exec_many
        Fanout.Register(this);
    }

    internal record ExecManyItem(string Host, string? RunId, int ExitCode, bool Timeout, string Output, string? Error);
    internal record ExecManyOutput(IReadOnlyList<ExecManyItem> Results);

    internal McpPrincipal? PrincipalFrom(McpCallContext context) => null; // TODO：principal 主机集合在传输层构造，见 McpHttpMount

    internal ISubSshStoreFull Store => _store;
    internal EventBus Bus => _bus;
    internal IConnectionPool? Pool => _pool;
    internal ICommandRunner? Runner => _runner;
}
