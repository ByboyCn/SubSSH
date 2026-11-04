// SubSsh.Plugin.Denylist — 令牌级工具拒绝组件插件（apps 层，可插拔、可任意替换）。
// 全量移植 OneSSH internal/mcpserver/denylist.go：
// GetEffectiveDisabledAsync = effectiveDisabled（实例级 ONESSH_DISABLED_TOOLS 与令牌级
// denylist 取并集；令牌只可收紧；令牌解析失败 fail-closed 禁用全部工具组）；
// IsToolAllowedAsync = installTokenDenylist 中间件的 tools/call 分支（工具名解析不出时
// fail-closed 拒绝）；AuditDeniedToolAsync = auditDeniedTool（写审计 + 发布 denied 事件）。
// 说明：OneSSH denylist.go 的"规则"即工具分组 denylist（前缀命名的分组常量与工具表，
// 1:1 见 SubSsh.Core.ToolGroups），不含额外的正则规则；解析失败一律 fail-closed。
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;
using SubSsh.Core.Events;
using SubSsh.Core.ToolGroups;

namespace SubSsh.Plugin.Denylist;

[Plugin("denylist", "令牌级工具拒绝：实例级+令牌级 denylist 并集、fail-closed、拒绝审计（可被同接口插件替换）")]
public sealed class DenylistPlugin : IComponent
{
    public string Name => "denylist";

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        DenylistService? svc = null;
        // 实例级 disabled tools 来自设置（对应 Go s.disabledTools ← ONESSH_DISABLED_TOOLS）。
        ctx.Require<ISettings>(
            settings =>
            {
                if (svc == null) svc = new DenylistService(settings);
                ctx.Provide<IDenylist>(svc);
                // 审计落库走公共 DB 组件；Db 插件下线时静默跳过持久化（对应 Go s.Store == nil 分支）。
                ctx.Require<IOneSshStoreFull>(svc.AttachStore, svc.DetachStore);
            },
            () => ctx.Unprovide<IDenylist>());
        return Task.CompletedTask;
    }
}

/// <summary>对应 Go events.Publish("tool_call", {"tool", "ok": false, "duration_ms": 0, "denied": true})。</summary>
public sealed record ToolCallDeniedEvent(string Tool, bool Ok, long DurationMs, bool Denied);
