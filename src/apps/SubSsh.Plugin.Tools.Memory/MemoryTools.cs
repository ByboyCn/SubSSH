// 记忆工具实现（对应 Go mcpserver/memory.go 的 handler 面）。
// bank 语义照抄 Go memoryBank：host 为空 → 全局 bank；否则 AuthorizedHost 解析到主机 bank。
// 授权：authorizedMemory 对应 Go authorizedMemory（全局记忆任意已认证令牌可改；主机 bank 记忆
// 要求令牌此刻仍能访问该主机），错误文案照抄。
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.Tools.Memory;

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

    public static double? DoubleOrNull(JsonElement a, string key) =>
        a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}

/// <summary>访问控制（对应 Go mcpserver/auth.go / memory.go；文案照抄）。</summary>
internal static class Access
{
    public static async Task<ApiToken> TokenAsync(ISubSshStoreFull store, McpCallContext ctx, CancellationToken ct)
    {
        var token = ctx.TokenId > 0 ? await store.GetTokenAsync(ctx.TokenId, ct).ConfigureAwait(false) : null;
        if (token == null) throw new SubSshException("unauthorized");
        return token;
    }

    public static async Task<bool> CanAccessHostAsync(ISubSshStoreFull store, ApiToken token, long hostId, CancellationToken ct)
    {
        if (token.AllHosts) return true;
        var ids = await store.TokenHostIdsAsync(token.Id, ct).ConfigureAwait(false);
        return ids.Contains(hostId);
    }

    /// <summary>令牌可见主机集合（对应 Go Principal.Hosts；已归档主机不可见）。</summary>
    public static async Task<IReadOnlyList<HostConfig>> TokenHostsAsync(ISubSshStoreFull store, ApiToken token, CancellationToken ct)
    {
        if (token.AllHosts)
            return (await store.ListHostsAsync(ct).ConfigureAwait(false)).Where(h => h.ArchivedAt == null).ToList();
        var ids = await store.TokenHostIdsAsync(token.Id, ct).ConfigureAwait(false);
        var hosts = new List<HostConfig>();
        foreach (var id in ids)
        {
            var h = await store.GetHostAsync(id, ct).ConfigureAwait(false);
            if (h != null && h.ArchivedAt == null) hosts.Add(h);
        }
        return hosts;
    }

    /// <summary>对应 Go AuthorizedHost（unknown host / host archived / host not authorized）。</summary>
    public static async Task<HostConfig> AuthorizedHostAsync(ISubSshStoreFull store, McpCallContext ctx, string name, CancellationToken ct)
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
}

// ---------------------------------------------------------------------------
// 工具 handler（对应 Go memory.go 的 registerMemory / memoryBank / authorizedMemory）
// ---------------------------------------------------------------------------
internal sealed class MemoryTools(ISubSshStoreFull store, IMemoryEngine engine, IDenylist? denylist)
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

    /// <summary>对应 Go memoryBank：host 空 → (null, "global")；否则授权解析 → (hostId, hostName)。</summary>
    private static async Task<(long? HostId, string Bank)> MemoryBankAsync(
        ISubSshStoreFull store, McpCallContext ctx, string host, CancellationToken ct)
    {
        if (host.Length == 0)
        {
            // 对应 Go FromContext !ok → "unauthorized"
            await Access.TokenAsync(store, ctx, ct).ConfigureAwait(false);
            return (null, "global");
        }
        var authorized = await Access.AuthorizedHostAsync(store, ctx, host, ct).ConfigureAwait(false);
        return (authorized.Id, authorized.Name);
    }

    /// <summary>对应 Go authorizedMemory（错误文案照抄）。</summary>
    private async Task<MemoryRecord> AuthorizedMemoryAsync(McpCallContext ctx, long id, CancellationToken ct)
    {
        var memory = await store.GetMemoryAsync(id, ct).ConfigureAwait(false);
        if (memory == null) throw new SubSshException("memory 不存在: " + id);
        var token = ctx.TokenId > 0 ? await store.GetTokenAsync(ctx.TokenId, ct).ConfigureAwait(false) : null;
        if (token == null) throw new SubSshException("memory 不可访问: " + id);
        if (memory.HostId == null) return memory;
        var host = await store.GetHostAsync(memory.HostId.Value, ct).ConfigureAwait(false);
        var allowed = host != null && host.ArchivedAt == null
            && await Access.CanAccessHostAsync(store, token, host.Id, ct).ConfigureAwait(false);
        if (!allowed) throw new SubSshException("memory 不可访问: " + id);
        return memory;
    }

    public Task<McpToolResult> MemoryRememberAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("memory_remember", ctx, ct, async () =>
        {
            var (hostId, bank) = await MemoryBankAsync(store, ctx, Args.Str(args, "host"), ct).ConfigureAwait(false);
            var importance = Args.DoubleOrNull(args, "importance");
            var result = await engine.RememberAsync(new RememberInput
            {
                HostId = hostId,
                Content = Args.Str(args, "content"),
                Source = Args.Str(args, "source"),
                Importance = importance ?? 0.5,
                ImportanceSet = importance != null,
                Veracity = Args.Str(args, "veracity"),
                TokenId = ctx.TokenId != 0 ? ctx.TokenId : null,
            }, ct).ConfigureAwait(false);
            return McpToolResult.Text(ToolJson.Serialize(new Dictionary<string, object?>
            {
                ["id"] = result.Id,
                ["bank"] = bank,
                ["deduped"] = result.Deduped,
                ["embedded"] = result.Embedded,
            }, emitNull: true));
        });

    public Task<McpToolResult> MemoryRecallAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("memory_recall", ctx, ct, async () =>
        {
            var (hostId, bank) = await MemoryBankAsync(store, ctx, Args.Str(args, "host"), ct).ConfigureAwait(false);
            var (results, engineName) = await engine.RecallAsync(hostId, hostId != null, Args.Str(args, "query"),
                Args.Int(args, "limit"), ct).ConfigureAwait(false);
            // 对应 Go：结果里的主机 bank 记忆回填 bank 名
            var withBank = results.Select(r => r.HostId != null ? r with { Bank = bank } : r).ToList();
            return McpToolResult.Text(ToolJson.Serialize(new Dictionary<string, object?>
            {
                ["results"] = withBank,
                ["engine"] = engineName,
            }, emitNull: true));
        });

    public Task<McpToolResult> MemoryListAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("memory_list", ctx, ct, async () =>
        {
            var (hostId, bank) = await MemoryBankAsync(store, ctx, Args.Str(args, "host"), ct).ConfigureAwait(false);
            var limit = Args.Int(args, "limit");
            if (limit == 0) limit = 50;
            if (limit < 1) limit = 1;
            if (limit > 200) limit = 200;
            var offset = Args.Int(args, "offset");
            if (offset < 0) throw new SubSshException("offset 不能为负数");
            var memories = await store.ListMemoriesAsync(hostId, limit, offset, ct).ConfigureAwait(false);
            // Go store.Memory 的 json 面：host_id/token_id/last_recalled 为 "-"（不输出）
            var rows = memories.Select(m => new Dictionary<string, object?>
            {
                ["id"] = m.Id,
                ["content"] = m.Content,
                ["source"] = m.Source,
                ["importance"] = m.Importance,
                ["veracity"] = m.Veracity,
                ["created_at"] = m.CreatedAt,
                ["updated_at"] = m.UpdatedAt,
                ["recall_count"] = m.RecallCount,
            }).ToList();
            return McpToolResult.Text(ToolJson.Serialize(new Dictionary<string, object?>
            {
                ["memories"] = rows,
                ["bank"] = bank,
            }, emitNull: true));
        });

    public Task<McpToolResult> MemoryUpdateAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("memory_update", ctx, ct, async () =>
        {
            var id = Args.Long(args, "id");
            var hasContent = args.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String;
            var hasImportance = args.TryGetProperty("importance", out var importanceEl) && importanceEl.ValueKind == JsonValueKind.Number;
            var hasVeracity = args.TryGetProperty("veracity", out var veracityEl) && veracityEl.ValueKind == JsonValueKind.String;
            if (!hasContent && !hasImportance && !hasVeracity)
                throw new SubSshException("至少提供 content、importance 或 veracity 之一");
            var memory = await AuthorizedMemoryAsync(ctx, id, ct).ConfigureAwait(false);
            await engine.UpdateAsync(memory,
                hasContent ? contentEl.GetString() : null,
                hasImportance ? importanceEl.GetDouble() : null,
                hasVeracity ? veracityEl.GetString() : null, ct).ConfigureAwait(false);
            var refreshed = await store.GetMemoryAsync(id, ct).ConfigureAwait(false);
            if (refreshed == null) throw new SubSshException("memory 不存在: " + id);
            return McpToolResult.Text(ToolJson.Serialize(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["embedded"] = refreshed.Embedding is { Length: > 0 },
            }, emitNull: true));
        });

    public Task<McpToolResult> MemoryForgetAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("memory_forget", ctx, ct, async () =>
        {
            var id = Args.Long(args, "id");
            await AuthorizedMemoryAsync(ctx, id, ct).ConfigureAwait(false);
            await store.DeleteMemoryAsync(id, ct).ConfigureAwait(false);
            return McpToolResult.Text(ToolJson.Serialize(new Dictionary<string, object?> { ["deleted"] = true }, emitNull: true));
        });

    public Task<McpToolResult> MemoryStatsAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("memory_stats", ctx, ct, async () =>
        {
            var token = await Access.TokenAsync(store, ctx, ct).ConfigureAwait(false);
            // 对应 Go visible：令牌可见主机（已归档主机不在 Principal.Hosts 中，不出现）
            var visible = new Dictionary<long, string>();
            foreach (var h in await Access.TokenHostsAsync(store, token, ct).ConfigureAwait(false))
                visible[h.Id] = h.Name;
            var stats = await store.MemoryStatsAsync(ct).ConfigureAwait(false);
            long total = 0;
            var banks = new List<object?>();
            foreach (var stat in stats)
            {
                string bank = "global";
                if (stat.HostId != null)
                {
                    if (!visible.TryGetValue(stat.HostId.Value, out var name)) continue;
                    bank = name;
                }
                total += stat.Count;
                banks.Add(new Dictionary<string, object?>
                {
                    ["bank"] = bank,
                    ["count"] = stat.Count,
                    ["embedded"] = stat.Embedded,
                    ["last_written"] = stat.LastWritten,
                });
            }
            return McpToolResult.Text(ToolJson.Serialize(new Dictionary<string, object?>
            {
                ["total"] = total,
                ["banks"] = banks,
            }, emitNull: true));
        });

    public Task<McpToolResult> MemorySleepAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("memory_sleep", ctx, ct, async () =>
        {
            var (hostId, _) = await MemoryBankAsync(store, ctx, Args.Str(args, "host"), ct).ConfigureAwait(false);
            var report = await engine.SleepAsync(hostId, ct).ConfigureAwait(false);
            return McpToolResult.Text(ToolJson.Serialize(report, emitNull: true));
        });
}
