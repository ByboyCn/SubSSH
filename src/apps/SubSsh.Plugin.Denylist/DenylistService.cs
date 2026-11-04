// SubSsh.Plugin.Denylist — DenylistService：mcpserver/denylist.go 全量 C# 移植。
// effectiveDisabled：合并进程级 ONESSH_DISABLED_TOOLS 与当前 Bearer 令牌的 denylist。
// 令牌只能再收紧，不能重新打开实例已关闭的组。令牌 denylist 解析失败时 fail-closed：
// 禁用全部 MCP 工具组，绝不静默放开。
using SubSsh.Core.Abstractions;
using SubSsh.Core.Events;
using SubSsh.Core.ToolGroups;

namespace SubSsh.Plugin.Denylist;

public sealed class DenylistService : IDenylist
{
    private readonly ISettings _settings;
    private readonly object _storeGate = new();
    private IOneSshStoreFull? _store;

    /// <summary>denied 事件总线（tool_call denied；由 WebApi/McpCore 桥接到 SSE）。</summary>
    public EventBus Events { get; } = new();

    public DenylistService(ISettings settings)
    {
        _settings = settings;
    }

    public void AttachStore(IOneSshStoreFull store)
    {
        lock (_storeGate) _store = store;
    }

    public void DetachStore()
    {
        lock (_storeGate) _store = null;
    }

    /// <summary>合并实例级与令牌级禁用集合；令牌只可收紧（Go effectiveDisabled）。</summary>
    public Task<DisabledGroups> GetEffectiveDisabledAsync(McpCallContext? principal, CancellationToken ct = default)
    {
        var effective = new DisabledGroups(_settings.DisabledTools);
        if (principal != null)
        {
            DisabledGroups tokenDisabled;
            try
            {
                tokenDisabled = ParseList(principal.DisabledTools);
            }
            catch (SubSshException)
            {
                tokenDisabled = AllDisabled(); // fail-closed：解析失败禁用全部工具组
            }
            effective = DisabledGroups.Union(effective, tokenDisabled);
        }
        return Task.FromResult(effective);
    }

    /// <summary>拦截通过 MCP 声明的工具调用（installTokenDenylist 中间件的 tools/call 分支）。
    /// 解析不出工具名时 fail-closed：有 denylist 生效就不能放行空名绕过。</summary>
    public async Task<bool> IsToolAllowedAsync(McpCallContext? principal, string toolName, CancellationToken ct = default)
    {
        var disabled = await GetEffectiveDisabledAsync(principal, ct).ConfigureAwait(false);
        if (disabled.IsEmpty) return true;
        if (string.IsNullOrEmpty(toolName)) return false;
        return !disabled.HidesTool(toolName);
    }

    /// <summary>记录被拒绝的工具调用审计并发布事件（Go auditDeniedTool）。Store 不可用时跳过持久化。</summary>
    public Task AuditDeniedToolAsync(string toolName, McpCallContext? principal, CancellationToken ct = default)
    {
        var store = Store;
        if (store != null)
        {
            var a = new AuditEntry
            {
                Ts = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                Tool = toolName,
                ParamsJson = "{}",
                Ok = false,
                TokenId = principal != null && principal.TokenId != 0 ? principal.TokenId : null,
                TokenName = principal != null && principal.TokenId != 0 ? principal.TokenName : null,
            };
            _ = store.AddAuditAsync(a, CancellationToken.None);
        }
        Events.Publish(new ToolCallDeniedEvent(toolName, Ok: false, DurationMs: 0, Denied: true));
        return Task.CompletedTask;
    }

    private IOneSshStoreFull? Store
    {
        get { lock (_storeGate) return _store; }
    }

    /// <summary>解析分组名列表（对应 Go toolgroups.ParseList）；未知分组抛错，由调用方 fail-closed。</summary>
    private static DisabledGroups ParseList(IReadOnlyList<string> groupNames)
    {
        var parsed = new DisabledGroups();
        foreach (var name in groupNames)
        {
            var (definition, found) = ToolGroupCatalog.Lookup(name);
            if (!found)
            {
                throw new SubSshException($"未知工具分组: {name}");
            }
            parsed = DisabledGroups.Union(parsed, new DisabledGroups([definition!.Group]));
        }
        return parsed;
    }

    /// <summary>禁用全部分组（对应 Go toolgroups.AllDisabled）。</summary>
    private static DisabledGroups AllDisabled() => new(ToolGroupCatalog.GroupNames());
}
