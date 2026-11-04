// 后台任务工具实现（对应 Go mcpserver/jobs.go 的 handler 面）。
// job_start 附带 command_runs 审计记录（startCommandRun/finishCommandRun 等价，经 IOneSshStoreFull）；
// 授权：任务按 job_id 归属令牌，且要求令牌此刻仍能访问所在主机（对应 Go ownedJob，文案照抄）。
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.Tools.Jobs;

// ---------------------------------------------------------------------------
// 序列化 / 参数 / 授权小工具（插件自包含，不跨插件引用）
// ---------------------------------------------------------------------------
internal static class ToolJson
{
    /// <summary>omitempty 风格：null 不输出（对应 Go omitempty）。</summary>
    public static readonly JsonSerializerOptions OmitNull = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>无 omitempty 字段保持输出 null（对应 Go 无 omitempty 的指针字段）。</summary>
    public static readonly JsonSerializerOptions EmitNull = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(object value, bool emitNull = false) =>
        JsonSerializer.Serialize(value, emitNull ? EmitNull : OmitNull);
}

internal static class Args
{
    public static string Str(JsonElement a, string key) =>
        a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static int Int(JsonElement a, string key) =>
        a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    public static long Long(JsonElement a, string key) =>
        a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    public static Dictionary<string, string>? EnvOrNull(JsonElement a, string key)
    {
        if (!a.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Object) return null;
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in v.EnumerateObject())
            env[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
        return env;
    }
}

/// <summary>访问控制（对应 Go mcpserver/auth.go；文案照抄）。</summary>
internal static class Access
{
    public static async Task<ApiToken> TokenAsync(IOneSshStoreFull store, McpCallContext ctx, CancellationToken ct)
    {
        var token = ctx.TokenId > 0 ? await store.GetTokenAsync(ctx.TokenId, ct).ConfigureAwait(false) : null;
        if (token == null) throw new SubSshException("unauthorized");
        return token;
    }

    /// <summary>主机访问授权（对应 Go AuthorizedHost）。</summary>
    public static async Task<HostConfig> AuthorizedHostAsync(IOneSshStoreFull store, McpCallContext ctx, string name, CancellationToken ct)
    {
        var token = await TokenAsync(store, ctx, ct).ConfigureAwait(false);
        var host = await store.GetHostByNameAsync(name, ct).ConfigureAwait(false);
        if (host == null) throw new SubSshException("unknown host: " + name);
        if (host.ArchivedAt != null)
            throw new SubSshException("host archived: " + name + "（主机已归档，需管理员在控制台取消归档）");
        if (!await CanAccessHostAsync(store, token, host.Id, ct).ConfigureAwait(false))
            throw new SubSshException("host not authorized: " + name);
        return host;
    }

    public static async Task<bool> CanAccessHostAsync(IOneSshStoreFull store, ApiToken token, long hostId, CancellationToken ct)
    {
        if (token.AllHosts) return true;
        var ids = await store.TokenHostIdsAsync(token.Id, ct).ConfigureAwait(false);
        return ids.Contains(hostId);
    }
}

// ---------------------------------------------------------------------------
// 工具 handler（对应 Go jobs.go 的 registerJobs 与 ownedJob；异常统一转 isError，文案照抄）
// ---------------------------------------------------------------------------
internal sealed class JobsTools(IOneSshStoreFull store, IJobManager jobs, IDenylist? denylist)
{
    private async Task<McpToolResult> GuardAsync(string tool, McpCallContext ctx, CancellationToken ct, Func<Task<McpToolResult>> body)
    {
        try
        {
            // 令牌级 denylist 兜底（可选；实例级/令牌级工具组拦截由 MCP 核心完成）
            if (denylist != null && !await denylist.IsToolAllowedAsync(ctx, tool, ct).ConfigureAwait(false))
                return McpToolResult.Error("tool not allowed: " + tool);
            return await body().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 对应 Go errorResult(err.Error())
            return McpToolResult.Error(ex.Message);
        }
    }

    /// <summary>对应 Go ownedJob：任务归属令牌，且令牌此刻仍能访问所在主机（归档/移出授权后不能经任务触达）。</summary>
    private async Task<JobRecord> OwnedJobAsync(McpCallContext ctx, string id, CancellationToken ct)
    {
        var token = await Access.TokenAsync(store, ctx, ct).ConfigureAwait(false);
        var job = await store.GetJobAsync(id, ct).ConfigureAwait(false);
        if (job == null) throw new SubSshException("unknown job: " + id);
        if (job.TokenId != ctx.TokenId) throw new SubSshException("job not authorized: " + id);
        var host = await store.GetHostAsync(job.HostId, ct).ConfigureAwait(false);
        if (host == null) throw new SubSshException("job host not authorized: " + id);
        if (host.ArchivedAt != null)
            throw new SubSshException($"job host archived: {host.Name}（主机已归档，需管理员在控制台取消归档）");
        if (!await Access.CanAccessHostAsync(store, token, job.HostId, ct).ConfigureAwait(false))
            throw new SubSshException("job host not authorized: " + id);
        return job;
    }

    public Task<McpToolResult> JobStartAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("job_start", ctx, ct, async () =>
        {
            var token = await Access.TokenAsync(store, ctx, ct).ConfigureAwait(false);
            var host = await Access.AuthorizedHostAsync(store, ctx, Args.Str(args, "host"), ct).ConfigureAwait(false);
            var command = Args.Str(args, "command");
            if (string.IsNullOrWhiteSpace(command)) throw new SubSshException("command 不能为空");
            var cwd = Args.Str(args, "cwd");
            if (cwd.Length == 0) cwd = "~";
            var env = Args.EnvOrNull(args, "env");

            // 对应 Go startCommandRun：建用户可见命令记录（job_start 审计）
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var run = new CommandRun
            {
                Id = Guid.NewGuid().ToString("D"),
                Tool = "job_start",
                HostId = host.Id,
                Host = host.Name,
                Command = command,
                Cwd = cwd,
                Status = "running",
                StartedAt = now,
                TokenId = ctx.TokenId > 0 ? ctx.TokenId : null,
                TokenName = ctx.TokenId > 0 ? token.Name : null,
            };
            await store.CreateCommandRunAsync(run, ct).ConfigureAwait(false);

            JobRecord job;
            try
            {
                // 对应 Go m.StartTracked：任务与命令记录按 job_id 关联落库
                job = await jobs.StartTrackedAsync(host, ctx.TokenId, command, cwd, env, run.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 对应 Go finishCommandRun：失败也要可靠落最终态，再回错误文案
                await store.FinishCommandRunAsync(run.Id, new CommandRunFinish
                {
                    Status = "failed",
                    ErrorText = ex.Message,
                    FinishedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                }, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            return McpToolResult.Text(ToolJson.Serialize(new Dictionary<string, object?>
            {
                ["job_id"] = job.Id,
                ["run_id"] = run.Id,
                ["pid"] = job.Pid,
            }, emitNull: true));
        });

    public Task<McpToolResult> JobListAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("job_list", ctx, ct, async () =>
        {
            var token = await Access.TokenAsync(store, ctx, ct).ConfigureAwait(false);
            long? hostId = null;
            var hostName = Args.Str(args, "host");
            if (hostName.Length > 0)
            {
                var host = await Access.AuthorizedHostAsync(store, ctx, hostName, ct).ConfigureAwait(false);
                hostId = host.Id;
            }
            var list = await store.ListJobsAsync(ctx.TokenId, hostId, ct).ConfigureAwait(false);
            var statuses = await jobs.RefreshAllAsync(list, ct).ConfigureAwait(false);
            return McpToolResult.Text(ToolJson.Serialize(new Dictionary<string, object?> { ["jobs"] = statuses }, emitNull: true));
        });

    public Task<McpToolResult> JobStatusAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("job_status", ctx, ct, async () =>
        {
            var job = await OwnedJobAsync(ctx, Args.Str(args, "job_id"), ct).ConfigureAwait(false);
            var status = await jobs.RefreshAsync(job, ct).ConfigureAwait(false);
            return McpToolResult.Text(ToolJson.Serialize(status, emitNull: true));
        });

    public Task<McpToolResult> JobLogsAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("job_logs", ctx, ct, async () =>
        {
            var job = await OwnedJobAsync(ctx, Args.Str(args, "job_id"), ct).ConfigureAwait(false);
            var output = await jobs.LogsAsync(job, Args.Int(args, "tail_lines"), Args.Str(args, "grep"),
                Args.Long(args, "offset_bytes"), ct).ConfigureAwait(false);
            return McpToolResult.Text(ToolJson.Serialize(new Dictionary<string, object?> { ["output"] = output }));
        });

    public Task<McpToolResult> JobKillAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("job_kill", ctx, ct, async () =>
        {
            var job = await OwnedJobAsync(ctx, Args.Str(args, "job_id"), ct).ConfigureAwait(false);
            var signal = Args.Str(args, "signal");
            if (signal.Length == 0) signal = "TERM";
            await jobs.KillAsync(job, signal, ct).ConfigureAwait(false);
            return McpToolResult.Text(ToolJson.Serialize(new Dictionary<string, object?> { ["ok"] = true }));
        });
}
