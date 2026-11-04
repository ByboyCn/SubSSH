// 主机工具实现（对应 Go mcpserver/hosts.go 的 handler 面）。
// 权限：McpCallContext 当前无 ManageHosts 位，经 ISubSshStoreFull.GetTokenAsync 查
// ApiToken.ManageHosts 实现校验，语义与错误文案对齐 Go auth.go 的 AuthorizedHostManagement。
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.Tools.Hosts;

// ---------------------------------------------------------------------------
// 序列化 / 参数 / 授权 / 注册小工具（插件自包含，不跨插件引用）
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

    public static long? LongOrNull(JsonElement a, string key) =>
        a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    public static bool? BoolOrNull(JsonElement a, string key) =>
        a.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;

    public static string[]? StrArrayOrNull(JsonElement a, string key)
    {
        if (!a.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? "");
        return [.. list];
    }
}

/// <summary>
/// 访问控制（对应 Go mcpserver/auth.go）：
/// - TokenAsync：对应 FromContext；令牌查不到视为未认证（"unauthorized"）。
/// - RequireManageHostsAsync：对应 AuthorizedHostManagement，错误文案照抄。
/// - AuthorizedHostAsync：对应 AuthorizedHost（unknown host / host archived / host not authorized）。
/// </summary>
internal static class Access
{
    public static async Task<ApiToken> TokenAsync(ISubSshStoreFull store, McpCallContext ctx, CancellationToken ct)
    {
        var token = ctx.TokenId > 0 ? await store.GetTokenAsync(ctx.TokenId, ct).ConfigureAwait(false) : null;
        if (token == null) throw new SubSshException("unauthorized");
        return token;
    }

    /// <summary>manage_hosts 校验（对应 Go AuthorizedHostManagement；错误文案照抄）。</summary>
    public static async Task<ApiToken> RequireManageHostsAsync(ISubSshStoreFull store, McpCallContext ctx, CancellationToken ct)
    {
        var token = await TokenAsync(store, ctx, ct).ConfigureAwait(false);
        if (!token.ManageHosts) throw new SubSshException("host management not authorized");
        return token;
    }

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

    public static async Task<bool> CanAccessHostAsync(ISubSshStoreFull store, ApiToken token, long hostId, CancellationToken ct)
    {
        if (token.AllHosts) return true;
        var ids = await store.TokenHostIdsAsync(token.Id, ct).ConfigureAwait(false);
        return ids.Contains(hostId);
    }

    /// <summary>令牌可见主机集合（对应 Go Principal.Hosts：已归档主机不可见）。</summary>
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
}

// ---------------------------------------------------------------------------
// 工具 handler（每个 handler 对应 Go hosts.go 的同名方法；异常统一转 isError，文案照抄）
// ---------------------------------------------------------------------------
internal sealed class HostsTools(ISubSshStoreFull store, IHostManager manager, IConnectionPool? pool, IDenylist? denylist)
{
    private async Task<McpToolResult> GuardAsync(string tool, McpCallContext ctx, CancellationToken ct, Func<Task<McpToolResult>> body)
    {
        try
        {
            // 令牌级 denylist 兜底（可选；实例级/令牌级工具组拦截由 MCP 核心完成，对应 Go installTokenDenylist）
            if (denylist != null && !await denylist.IsToolAllowedAsync(ctx, tool, ct).ConfigureAwait(false))
                return McpToolResult.Error("tool not allowed: " + tool);
            return await body().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 对应 Go errorResult(err.Error())：业务失败转 isError 文本内容
            return McpToolResult.Error(ex.Message);
        }
    }

    /// <summary>Go net.JoinHostPort。</summary>
    private static string JoinHostPort(string addr, int port) =>
        addr.Contains(':') ? $"[{addr}]:{port}" : $"{addr}:{port}";

    /// <summary>对应 Go hostByName。</summary>
    private static async Task<HostConfig> HostByNameAsync(ISubSshStoreFull store, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new SubSshException("host 不能为空");
        var host = await store.GetHostByNameAsync(name, ct).ConfigureAwait(false);
        if (host == null) throw new SubSshException("unknown host: " + name);
        return host;
    }

    /// <summary>对应 Go managedHostItem（jump_host 解析为名称；emitNull 保持 Go 无 omitempty 字段的 null）。</summary>
    private async Task<Dictionary<string, object?>> ManagedItemAsync(HostConfig host, CancellationToken ct)
    {
        string? jump = null;
        if (host.JumpHostId != null)
        {
            var j = await store.GetHostAsync(host.JumpHostId.Value, ct).ConfigureAwait(false);
            if (j != null) jump = j.Name;
        }
        return new Dictionary<string, object?>
        {
            ["id"] = host.Id,
            ["name"] = host.Name,
            ["addr"] = host.Addr,
            ["port"] = host.Port,
            ["username"] = host.Username,
            ["auth_type"] = host.AuthType,
            ["key_id"] = host.KeyId,
            ["hostkey_fp"] = host.HostKeyFp,
            ["jump_host"] = jump,
            ["monitor_enabled"] = host.MonitorEnabled,
            ["tags"] = host.Tags,
            ["created_at"] = host.CreatedAt,
            ["online"] = pool?.IsOnline(host.Name) ?? false,
            ["archived"] = host.ArchivedAt != null,
        };
    }

    private static HostInput HostInputFrom(JsonElement a) => new()
    {
        Name = Args.Str(a, "name"),
        Addr = Args.Str(a, "addr"),
        Port = Args.Int(a, "port"),
        Username = Args.Str(a, "username"),
        AuthType = Args.Str(a, "auth_type"),
        KeyId = Args.LongOrNull(a, "key_id"),
        Password = Args.Str(a, "password") is { Length: > 0 } p ? p : null,
        JumpHost = Args.Str(a, "jump_host") is { Length: > 0 } j ? j : null,
        MonitorEnabled = Args.BoolOrNull(a, "monitor_enabled"),
        Tags = Args.StrArrayOrNull(a, "tags"),
    };

    public Task<McpToolResult> HostsListAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("hosts_list", ctx, ct, async () =>
        {
            var token = await Access.TokenAsync(store, ctx, ct).ConfigureAwait(false);
            var hosts = await Access.TokenHostsAsync(store, token, ct).ConfigureAwait(false);
            var items = hosts.Select(h => new Dictionary<string, object?>
            {
                ["name"] = h.Name,
                ["addr"] = JoinHostPort(h.Addr, h.Port),
                ["username"] = h.Username,
                ["online"] = pool?.IsOnline(h.Name) ?? false,
                ["tags"] = h.Tags ?? [],
            }).ToList();
            return McpToolResult.Text(ToolJson.Serialize(new Dictionary<string, object?> { ["hosts"] = items }, emitNull: true));
        });

    public Task<McpToolResult> HostsManageListAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("hosts_manage_list", ctx, ct, async () =>
        {
            await Access.RequireManageHostsAsync(store, ctx, ct).ConfigureAwait(false);
            var hosts = await store.ListHostsAsync(ct).ConfigureAwait(false);
            var items = new List<object?>();
            foreach (var h in hosts) items.Add(await ManagedItemAsync(h, ct).ConfigureAwait(false));
            return McpToolResult.Text(ToolJson.Serialize(new Dictionary<string, object?> { ["hosts"] = items }, emitNull: true));
        });

    public Task<McpToolResult> HostCreateAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("host_create", ctx, ct, async () =>
        {
            await Access.RequireManageHostsAsync(store, ctx, ct).ConfigureAwait(false);
            var host = await manager.CreateAsync(HostInputFrom(args), ct).ConfigureAwait(false);
            return McpToolResult.Text(ToolJson.Serialize(await ManagedItemAsync(host, ct).ConfigureAwait(false), emitNull: true));
        });

    public Task<McpToolResult> HostUpdateAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("host_update", ctx, ct, async () =>
        {
            await Access.RequireManageHostsAsync(store, ctx, ct).ConfigureAwait(false);
            var host = await HostByNameAsync(store, Args.Str(args, "host"), ct).ConfigureAwait(false);
            var updated = await manager.UpdateAsync(host.Id, HostInputFrom(args), ct).ConfigureAwait(false);
            return McpToolResult.Text(ToolJson.Serialize(await ManagedItemAsync(updated, ct).ConfigureAwait(false), emitNull: true));
        });

    public Task<McpToolResult> HostTestAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("host_test", ctx, ct, async () =>
        {
            await Access.RequireManageHostsAsync(store, ctx, ct).ConfigureAwait(false);
            var host = await HostByNameAsync(store, Args.Str(args, "host"), ct).ConfigureAwait(false);
            var result = await manager.TestAsync(host.Id, ct).ConfigureAwait(false);
            return McpToolResult.Text(ToolJson.Serialize(result));
        });

    public Task<McpToolResult> HostResetFingerprintAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("host_reset_fingerprint", ctx, ct, async () =>
        {
            await Access.RequireManageHostsAsync(store, ctx, ct).ConfigureAwait(false);
            var host = await HostByNameAsync(store, Args.Str(args, "host"), ct).ConfigureAwait(false);
            await manager.ResetFingerprintAsync(host.Id, ct).ConfigureAwait(false);
            return McpToolResult.Text(ToolJson.Serialize(new Dictionary<string, object?> { ["ok"] = true }));
        });

    public Task<McpToolResult> HostDeleteAsync(JsonElement args, McpCallContext ctx, CancellationToken ct) =>
        GuardAsync("host_delete", ctx, ct, async () =>
        {
            await Access.RequireManageHostsAsync(store, ctx, ct).ConfigureAwait(false);
            var host = await HostByNameAsync(store, Args.Str(args, "host"), ct).ConfigureAwait(false);
            await manager.DeleteAsync(host.Id, ct).ConfigureAwait(false);
            return McpToolResult.Text(ToolJson.Serialize(new Dictionary<string, object?> { ["ok"] = true }));
        });
}
