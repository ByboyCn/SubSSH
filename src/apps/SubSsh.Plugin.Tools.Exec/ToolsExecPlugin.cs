// SubSsh.Plugin.Tools.Exec — exec 工具组插件（对应 OneSSH internal/mcpserver/exec.go）。
// GPL-3.0。Require: IMcpServer + IToolRegistry（工具经 Track 注册，卸载随 McpCore 级联反注册）
// + ICommandRunner + IConnectionPool + IOneSshStoreFull（必需）；IDenylist（可选）+ IToolGroups（可选）。
// exec_many（fanout）已由 Plugin.McpCore 内置注册（McpServerCore.RegisterBuiltinTools），此处不重复注册。
using System.Text.Json;
using System.Text.RegularExpressions;
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;
using SubSsh.Core.Models;
using SubSsh.Core.ToolGroups;
using HostConfig = SubSsh.Core.Abstractions.HostConfig;
using CommandRun = SubSsh.Core.Abstractions.CommandRun;
using CommandRunFinish = SubSsh.Core.Abstractions.CommandRunFinish;

namespace SubSsh.Plugin.Tools.Exec;

[Plugin("tools-exec", "exec 工具组：exec / session_env / output_read（exec_many 由 McpCore fanout 注册）")]
public sealed class ToolsExecPlugin : IComponent
{
    public string Name => "tools-exec";

    private IToolRegistry? _registry;
    private ICommandRunner? _runner;
    private IConnectionPool? _pool;
    private IOneSshStoreFull? _store;
    private IDenylist? _denylist;
    private bool _registered;
    private readonly object _gate = new();

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        // 必需依赖齐备才注册工具；任一下线由 McpCore 卸载管线级联撤回（IToolRegistry 无显式反注册 API）
        void Publish()
        {
            if (_registry == null || _runner == null || _pool == null || _store == null) return;
            lock (_gate)
            {
                if (_registered) return;
                _registered = true;
            }
            // 时间可组合：注册登记为可撤销效果（apply 幂等，重复调用由 _registered 守卫）
            ctx.Track(
                apply: () => RegisterToolsAsync().GetAwaiter().GetResult(),
                revert: () => { });
        }

        ctx.Require<IToolRegistry>(r => { _registry = r; Publish(); }, () => { _registry = null; });
        ctx.Require<ICommandRunner>(r => { _runner = r; Publish(); }, () => { _runner = null; });
        ctx.Require<IConnectionPool>(r => { _pool = r; Publish(); }, () => { _pool = null; });
        ctx.Require<IOneSshStoreFull>(r => { _store = r; Publish(); }, () => { _store = null; });
        // 可选依赖：IDenylist 在线时做第二道拦截 + denied 审计；IToolGroups 暂不直接使用（禁用组由 McpCore 统一过滤）
        ctx.Require<IDenylist>(d => _denylist = d, () => _denylist = null);
        ctx.Require<IToolGroups>(_ => { }, () => { });

        return Task.CompletedTask;
    }

    private async Task RegisterToolsAsync()
    {
        var registry = _registry!;
        // exec_many（fanout）已由 Plugin.McpCore 内置注册（McpServerCore.RegisterBuiltinTools），勿重复注册
        var execManyRegistered = await registry.IsRegisteredAsync(ToolNames.ExecMany).ConfigureAwait(false);
        _ = execManyRegistered;
        McpToolHandler execHandler = ExecAsync;
        await registry.RegisterAsync(new McpToolDescriptor("exec", Description("在一台授权主机上同步执行 shell 命令，返回 stdout、stderr、合并后的 output、退出码与执行后的工作目录。"
                + "工作目录按 host+session 持久保存：这次 cd 到哪里，同一 session 的下次 exec 就从哪里开始；环境变量用 session_env 维护。"
                + "适合秒级到分钟级的命令，超过 timeout_s（默认 60、上限 600）会中断并返回 timeout=true，长任务改用 job_start。"
                + "输出超过 max_lines 或 256KiB 时 truncated=true 并给出 artifact_id，用 output_read 翻页或过滤，不要重跑命令加 head/tail。"
                + "读写文件、搜索、跨主机复制请优先用 file_read/file_write/file_edit/file_list/grep/find/file_transfer，它们比在 exec 里拼命令更不容易出错。"),
            Schema("""
                {
                  "type": "object",
                  "properties": {
                    "host": { "type": "string", "description": "SSH 主机名，取自 hosts_list" },
                    "command": { "type": "string", "description": "原样交给远端 shell 执行的命令；不必自行 cd，工作目录由会话保持" },
                    "session": { "type": "string", "description": "持久会话标签，默认 default；不同标签之间不共享工作目录和环境变量" },
                    "timeout_s": { "type": "integer", "description": "超时秒数，默认 60，上限 600；超时返回 timeout=true 且远端命令被中断" },
                    "max_lines": { "type": "integer", "description": "output 返回的最大行数，默认 200；超出部分只在 artifact 中" },
                    "tail": { "type": "boolean", "description": "true 返回末尾若干行，默认返回开头若干行" }
                  },
                  "required": ["host", "command"],
                  "additionalProperties": false
                }
                """)),
            execHandler).ConfigureAwait(false);

        McpToolHandler sessionEnvHandler = SessionEnvAsync;
        await registry.RegisterAsync(new McpToolDescriptor("session_env", Description("设置或删除某台主机某个持久 exec 会话的环境变量，返回该会话的全部环境变量。"
                + "设置后每次 exec 都会先 export 这些变量，适合固定 PATH、代理、语言等；一次性变量直接写在 exec 命令里即可。"
                + "job_start 不读取会话环境，需要在它自己的 env 参数里单独传。不要在这里存放密码或令牌。"),
            Schema("""
                {
                  "type": "object",
                  "properties": {
                    "host": { "type": "string", "description": "SSH 主机名，取自 hosts_list" },
                    "session": { "type": "string", "description": "持久会话标签，留空表示 default" },
                    "set": { "type": "object", "additionalProperties": { "type": "string" }, "description": "要设置的环境变量；变量名必须匹配 [A-Za-z_][A-Za-z0-9_]*" },
                    "unset": { "type": "array", "items": { "type": "string" }, "description": "要删除的环境变量名" }
                  },
                  "required": ["host", "session"],
                  "additionalProperties": false
                }
                """)),
            sessionEnvHandler).ConfigureAwait(false);

        McpToolHandler outputReadHandler = OutputReadAsync;
        await registry.RegisterAsync(new McpToolDescriptor("output_read", Description("读取 exec 因输出过大而落在网关本地的完整输出（artifact）。返回带原始行号的内容和总行数，可用 offset/limit 翻页、用 grep 正则过滤。"
                + "artifact 保留 7 天，过期后返回错误，此时重新执行命令即可。"),
            Schema("""
                {
                  "type": "object",
                  "properties": {
                    "artifact_id": { "type": "string", "description": "exec 返回的 artifact_id，保留 7 天" },
                    "offset": { "type": "integer", "description": "起始序号，从 1 开始，默认 1；配合 grep 时按匹配结果计数" },
                    "limit": { "type": "integer", "description": "最大返回行数，默认 200，上限 5000" },
                    "grep": { "type": "string", "description": "Go 正则，只返回匹配的行" }
                  },
                  "required": ["artifact_id"],
                  "additionalProperties": false
                }
                """)),
            outputReadHandler).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // exec（对应 Go registerExec 的 exec 分支）
    // ------------------------------------------------------------------
    private async Task<McpToolResult> ExecAsync(JsonElement args, McpCallContext context, CancellationToken ct)
    {
        var denied = await DeniedAsync("exec", context, ct).ConfigureAwait(false);
        if (denied != null) return denied;

        var store = _store!; var runner = _runner!; var pool = _pool!;
        HostConfig h;
        try { h = await AuthorizedHostAsync(store, context, Str(args, "host"), ct).ConfigureAwait(false); }
        catch (SubSshException ex) { return McpToolResult.Error(ex.Message); }

        var command = Str(args, "command");
        if (command.Trim().Length == 0)
            return McpToolResult.Error("command 不能为空");

        var label = Str(args, "session");
        if (label.Length == 0) label = "default";

        SessionRecord state;
        try { state = await store.GetSessionAsync(context.TokenId, h.Id, label, ct).ConfigureAwait(false); }
        catch (Exception ex) { return McpToolResult.Error(ex.Message); }

        var run = await StartCommandRunAsync(store, context, "exec", h, command, state.Cwd, label, ct).ConfigureAwait(false);

        IRemoteConnection? client = null;
        try
        {
            client = await pool.GetAsync(h.Name, ct).ConfigureAwait(false);
            var timeoutS = Int(args, "timeout_s");
            if (timeoutS <= 0) timeoutS = 60;
            if (timeoutS > 600) timeoutS = 600;
            var maxLines = Int(args, "max_lines");
            if (maxLines <= 0) maxLines = 200;

            Exception? runErr = null;
            ExecResult res = new();
            try
            {
                // TODO：OnOutput → command_output 实时事件（EventBus 由 McpCore 私有持有，IContext 暂不暴露）
                res = await runner.RunAsync(client, command, state.Cwd, state.Env, new ExecOptions
                {
                    Timeout = TimeSpan.FromSeconds(timeoutS),
                    MaxLines = maxLines,
                    Tail = Bool(args, "tail"),
                    CaptureId = run.Id,
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { runErr = ex; }

            await FinishCommandRunAsync(store, run, res, runErr, ct).ConfigureAwait(false);

            if (runErr != null)
                return McpToolResult.Error(runErr.Message);

            if (res.Cwd.Length > 0 && !res.Timeout)
            {
                state.Cwd = res.Cwd;
                _ = SaveSessionQuietly(store, state);
            }
            return McpToolResult.Text(ExecOutputJson(run.Id, res));
        }
        catch (Exception ex)
        {
            await FinishCommandRunAsync(store, run, new ExecResult(), ex, ct).ConfigureAwait(false);
            return McpToolResult.Error(ex.Message);
        }
        finally
        {
            if (client != null) await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task SaveSessionQuietly(IOneSshStoreFull store, SessionRecord state)
    {
        try { await store.SaveSessionAsync(state).ConfigureAwait(false); }
        catch { /* 对应 Go _ = s.Store.SaveSession(...) */ }
    }

    // ------------------------------------------------------------------
    // session_env（对应 Go Server.sessionEnv）
    // ------------------------------------------------------------------
    private async Task<McpToolResult> SessionEnvAsync(JsonElement args, McpCallContext context, CancellationToken ct)
    {
        var denied = await DeniedAsync("session_env", context, ct).ConfigureAwait(false);
        if (denied != null) return denied;

        var store = _store!;
        HostConfig h;
        try { h = await AuthorizedHostAsync(store, context, Str(args, "host"), ct).ConfigureAwait(false); }
        catch (SubSshException ex) { return McpToolResult.Error(ex.Message); }

        var session = Str(args, "session");
        if (session.Length == 0) session = "default";

        SessionRecord state;
        try { state = await store.GetSessionAsync(context.TokenId, h.Id, session, ct).ConfigureAwait(false); }
        catch (Exception ex) { return McpToolResult.Error(ex.Message); }
        state.Env ??= [];

        var valid = new Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
        if (args.TryGetProperty("set", out var set) && set.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in set.EnumerateObject())
            {
                if (!valid.IsMatch(prop.Name))
                    return McpToolResult.Error($"环境变量名无效: {prop.Name}");
                if (prop.Value.ValueKind == JsonValueKind.String)
                    state.Env[prop.Name] = prop.Value.GetString() ?? "";
            }
        }
        if (args.TryGetProperty("unset", out var unset) && unset.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in unset.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    state.Env.Remove(item.GetString() ?? "");
        }

        try { await store.SaveSessionAsync(state, ct).ConfigureAwait(false); }
        catch (Exception ex) { return McpToolResult.Error(ex.Message); }

        return McpToolResult.Text(Json(new SessionEnvOutput { Env = state.Env }));
    }

    private sealed record SessionEnvOutput
    {
        [System.Text.Json.Serialization.JsonPropertyName("env")]
        public Dictionary<string, string> Env { get; init; } = [];
    }

    // ------------------------------------------------------------------
    // output_read（对应 Go registerExec 的 output_read 分支）
    // ------------------------------------------------------------------
    private async Task<McpToolResult> OutputReadAsync(JsonElement args, McpCallContext context, CancellationToken ct)
    {
        var runner = _runner!;
        string path;
        try { path = await runner.ArtifactPathAsync(Str(args, "artifact_id"), ct).ConfigureAwait(false); }
        catch (Exception ex) { return McpToolResult.Error(ex.Message); }

        try
        {
            var (content, total) = await runner.ReadArtifactAsync(
                path, Int(args, "offset"), Int(args, "limit"), Str(args, "grep"), ct).ConfigureAwait(false);
            return McpToolResult.Text(Json(new OutputReadOutput { Content = content, TotalLines = total }));
        }
        catch (Exception ex) { return McpToolResult.Error(ex.Message); }
    }

    private sealed record OutputReadOutput
    {
        [System.Text.Json.Serialization.JsonPropertyName("content")] public string Content { get; init; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("total_lines")] public int TotalLines { get; init; }
    }

    // ------------------------------------------------------------------
    // 命令执行记录（对应 Go startCommandRun / finishCommandRun / commandRunStatus；
    // 审计与脱敏由 McpCore 的 tools/call 包装统一完成，这里只落 store）
    // ------------------------------------------------------------------
    private static async Task<CommandRun> StartCommandRunAsync(
        IOneSshStoreFull store, McpCallContext context, string tool, HostConfig host,
        string command, string cwd, string session, CancellationToken ct)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var run = new CommandRun
        {
            Id = Guid.NewGuid().ToString("D"),
            Tool = tool,
            HostId = host.Id != 0 ? host.Id : null,
            Host = host.Name,
            Command = command,
            Cwd = cwd,
            Session = session.Length > 0 ? session : null,
            Status = "running",
            StartedAt = now,
            TokenId = context.TokenId != 0 ? context.TokenId : null,
            TokenName = context.TokenId != 0 ? context.TokenName : null,
        };
        await store.CreateCommandRunAsync(run, ct).ConfigureAwait(false);
        // TODO：发布 command_started 事件（EventBus 暂不经 IContext 暴露，见 McpCore）
        return run;
    }

    private static async Task FinishCommandRunAsync(
        IOneSshStoreFull store, CommandRun run, ExecResult result, Exception? runErr, CancellationToken requestCt)
    {
        // 状态机（对应 Go commandRunStatus，McpCore.CommandRuns.RunStatus 同形）
        string status;
        if (result.Timeout) status = requestCt.IsCancellationRequested ? "cancelled" : "timeout";
        else if (runErr != null) status = result.ExitCodeKnown ? "failed" : "failed";
        else status = result.ExitCode != 0 ? "failed" : "succeeded";

        long? exitCode = status is "cancelled" or "timeout"
            ? null
            : result.ExitCode >= 0 && (runErr == null || result.ExitCodeKnown) ? result.ExitCode : null;

        var finish = new CommandRunFinish
        {
            Status = status,
            ExitCode = exitCode,
            StdoutPreview = Preview(result.Stdout),
            StderrPreview = Preview(result.Stderr),
            StdoutBytes = result.StdoutBytes,
            StderrBytes = result.StderrBytes,
            OutputAvailable = result.OutputRecorded,
            OutputError = result.OutputCaptureError,
            ErrorText = runErr?.Message,
            FinishedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
        };
        try { await store.FinishCommandRunAsync(run.Id, finish).ConfigureAwait(false); }
        catch { /* 落最终态失败不阻断返回（后台 context 语义由实现保证） */ }
    }

    private static string Preview(string value)
    {
        const int limit = 64 << 10;
        if (value.Length <= limit) return value;
        var head = limit / 2;
        var tailStart = value.Length - (limit - head);
        return value[..head] + "\n\n… 中间输出已省略，完整内容请在命令详情中读取 …\n\n" + value[tailStart..];
    }

    /// <summary>ExecOutput JSON（Go 嵌入 execx.Result → run_id + 平铺字段）。</summary>
    private static string ExecOutputJson(string runId, ExecResult res)
    {
        var merged = JsonSerializer.Deserialize<Dictionary<string, object?>>(Json(res)) ?? [];
        merged["run_id"] = runId;
        return JsonSerializer.Serialize(merged, JsonOpts.Indented);
    }

    // ------------------------------------------------------------------
    // 公共小件
    // ------------------------------------------------------------------
    private async Task<McpToolResult?> DeniedAsync(string toolName, McpCallContext context, CancellationToken ct)
    {
        if (_denylist == null) return null;
        if (await _denylist.IsToolAllowedAsync(context, toolName, ct).ConfigureAwait(false)) return null;
        try { await _denylist.AuditDeniedToolAsync(toolName, context, ct).ConfigureAwait(false); }
        catch { /* 审计失败不阻断拒绝（对应 Go _ = s.Store.AddAudit） */ }
        return McpToolResult.Error("tool not authorized: " + toolName);
    }

    internal static async Task<HostConfig> AuthorizedHostAsync(
        IOneSshStoreFull store, McpCallContext context, string name, CancellationToken ct)
    {
        _ = context;
        // TODO：令牌级主机集合授权（McpPrincipal 在传输层构造，PrincipalFrom 当前恒 null，见 McpCore）
        var found = await store.GetHostByNameAsync(name, ct).ConfigureAwait(false);
        if (found == null) throw new SubSshException("unknown host: " + name);
        if (found.ArchivedAt != null)
            throw new SubSshException("host archived: " + name + "（主机已归档，需管理员在控制台取消归档）");
        return found;
    }

    private static string Str(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static bool Bool(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    internal static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOpts.Default);

    internal static McpToolDescriptor Tool(string name, string description, JsonElement schema, McpToolHandler handler)
        => new(name, description, schema);

    private static string Description(string text) => text;

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    internal static class JsonOpts
    {
        // 输出字段名与 Go json tag 一致（模型已带 JsonPropertyName；无 tag 的 record 用 snake_case）
        public static readonly JsonSerializerOptions Default = new()
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        };
        public static readonly JsonSerializerOptions Indented = new()
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
        };
    }
}
