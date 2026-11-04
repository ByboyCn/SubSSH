// 批量执行工具 exec_many（对应 mcpserver/fanout.go：并发上限 16、按输入顺序逐台返回、
// 每台输出截断到 4096 字节、经 command_runs 审计关联 run_ids）。
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.McpCore;

public static class Fanout
{
    public static void Register(McpServerCore server)
    {
        server.Registry.RegisterAsync(
            new McpToolDescriptor(
                Name: "exec_many",
                Description:
                    "在多台授权主机上并发执行同一条命令（并发上限 16），按输入顺序逐台返回退出码、超时标记和输出，单台失败或无权限只影响该条结果。" +
                    "适合批量巡检、统一查版本、同时重载配置；不同主机要跑不同命令时请分别调用 exec。" +
                    "与 exec 不同：固定在家目录执行，不使用持久会话，也不带会话环境变量；每台输出截断到 4096 字节且不会生成 artifact，需要完整输出请对单台再用 exec。" +
                    "批量修改是高风险操作，先在一台上验证再铺开。",
                InputSchema: System.Text.Json.JsonSerializer.SerializeToElement(
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            hosts = new { type = "array", items = new { type = "string" }, description = "目标主机名列表，取自 hosts_list；无权限的主机会在结果里单独报错" },
                            command = new { type = "string", description = "在每台主机上执行的同一条 shell 命令" },
                            timeout_s = new { type = "integer", description = "每台主机各自的超时秒数，默认 60，上限 600" },
                        },
                        required = new[] { "hosts", "command" },
                    }, McpJson.Options)),
            (args, ctx, ct) => CallAsync(server, args, ct));
    }

    private static async Task<McpToolResult> CallAsync(McpServerCore server, JsonElement arguments, CancellationToken ct)
    {
        var hosts = new List<string>();
        if (arguments.TryGetProperty("hosts", out var h) && h.ValueKind == JsonValueKind.Array)
            foreach (var item in h.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) hosts.Add(item.GetString() ?? "");
        var command = arguments.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? "" : "";
        var timeoutS = arguments.TryGetProperty("timeout_s", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetInt32() : 0;
        if (hosts.Count == 0) return McpToolResult.Error("hosts 不能为空");
        if (string.IsNullOrWhiteSpace(command)) return McpToolResult.Error("command 不能为空");

        var output = await ExecManyAsync(server, hosts, command, timeoutS, ct).ConfigureAwait(false);

        // 对应 Go ExecManyOutput.auditCommandRunIDs / auditOutcome：run_ids 关联与整体成败
        var extras = AuditExtras.Scope;
        foreach (var item in output.Results)
            if (item.RunId is { Length: > 0 }) extras.RunIds.Add(item.RunId);
        if (output.Results.Any(r => r.Error is { Length: > 0 } || r.Timeout || r.ExitCode != 0))
            extras.ExitCode = 1; // 任一台失败即整体失败（Go 返回 ok=false、exitCode=nil；此处以非零表达，TODO 对齐）
        extras.ExitCodeSet = true;

        var json = System.Text.Json.JsonSerializer.Serialize(
            new { results = output.Results.Select(r => new Dictionary<string, object?>
            {
                ["host"] = r.Host,
                ["run_id"] = r.RunId,
                ["exit_code"] = r.ExitCode,
                ["timeout"] = r.Timeout,
                ["output"] = r.Output,
                ["error"] = r.Error,
            }).ToList() }, McpJson.Options);
        return new McpToolResult([json], IsError: false);
    }

    private static async Task<McpServerCore.ExecManyOutput> ExecManyAsync(
        McpServerCore server, IReadOnlyList<string> hosts, string command, int timeoutS, CancellationToken ct)
    {
        if (timeoutS <= 0) timeoutS = 60;
        if (timeoutS > 600) timeoutS = 600;
        var results = new McpServerCore.ExecManyItem?[hosts.Count];
        using var gate = new SemaphoreSlim(16);
        var tasks = new List<Task>();
        for (var i = 0; i < hosts.Count; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try { results[idx] = await RunOneAsync(server, hosts[idx], command, timeoutS, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { results[idx] = new McpServerCore.ExecManyItem(hosts[idx], null, -1, false, "", ex.Message); }
                finally { gate.Release(); }
            }, ct));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        var final = new List<McpServerCore.ExecManyItem>(hosts.Count);
        for (var i = 0; i < hosts.Count; i++)
            final.Add(results[i] ?? new McpServerCore.ExecManyItem(hosts[i], null, -1, false, "", "internal error"));
        return new McpServerCore.ExecManyOutput(final);
    }

    private static async Task<McpServerCore.ExecManyItem> RunOneAsync(
        McpServerCore server, string name, string command, int timeoutS, CancellationToken requestCt)
    {
        var item = new McpServerCore.ExecManyItem(name, null, -1, false, "", null);
        HostConfig host;
        try
        {
            // TODO：经 McpPrincipal 做令牌级主机授权（AuthorizedHost）；当前仅查库存档校验
            var found = await server.Store.GetHostByNameAsync(name, requestCt).ConfigureAwait(false);
            if (found == null) return item with { Error = "unknown host: " + name };
            if (found.ArchivedAt != null)
                return item with { Error = "host archived: " + name + "（主机已归档，需管理员在控制台取消归档）" };
            host = found;
        }
        catch (Exception ex) { return item with { Error = ex.Message }; }

        CommandRun run;
        try
        {
            run = await CommandRuns.StartCommandRunAsync(
                server.Store, server.Bus, principal: null, "exec_many", host, command, "~", session: "", requestCt)
                .ConfigureAwait(false);
        }
        catch (Exception ex) { return item with { Error = ex.Message }; }
        item = item with { RunId = run.Id };

        IRemoteConnection client;
        try
        {
            client = await server.Pool!.GetAsync(name, requestCt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SafeFinishAsync(server, run, new ExecResult(), ex, requestCt).ConfigureAwait(false);
            return item with { Error = ex.Message };
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(requestCt);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutS));
        Exception? runErr = null;
        ExecResult res;
        try
        {
            res = await server.Runner!.RunAsync(client, command, "~", null,
                new ExecOptions { Timeout = TimeSpan.FromSeconds(timeoutS), MaxLines = 200, CaptureId = run.Id },
                timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            res = new ExecResult { Timeout = timeoutCts.IsCancellationRequested && !requestCt.IsCancellationRequested };
            runErr = ex;
        }
        await SafeFinishAsync(server, run, res, runErr, requestCt).ConfigureAwait(false);
        if (runErr != null)
            return item with { Error = runErr.Message };
        var output = res.Output.Length > 4096 ? TruncateUtf8(res.Output, 4096) : res.Output;
        return item with { ExitCode = res.ExitCode, Timeout = res.Timeout, Output = output };
    }

    private static async Task SafeFinishAsync(McpServerCore server, CommandRun run, ExecResult res, Exception? err, CancellationToken requestCt)
    {
        try { await CommandRuns.FinishCommandRunAsync(server.Store, server.Bus, run, res, err, requestCt).ConfigureAwait(false); }
        catch { /* 记录失败不影响结果回包（TODO：按 Go 版拼接 "; 记录执行结果失败: …"） */ }
    }

    /// <summary>按 UTF-8 字节上限截断并保证不切坏多字节字符（对应 Go execx.CompleteUTF8Prefix 的输出截断用法）。</summary>
    private static string TruncateUtf8(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes) return value;
        var cut = maxBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--; // 回退到字符起点
        return Encoding.UTF8.GetString(bytes, 0, cut);
    }
}
