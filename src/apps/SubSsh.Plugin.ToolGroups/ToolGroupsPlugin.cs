// SubSsh.Plugin.ToolGroups — 应用/功能组插件：发布 IToolGroups（工具组目录），
// 并把 Plugin.Db 的持久化边界校验 hook 接到 ToolGroupCatalog（fail-closed）。
// GPL-3.0，移植自 SubSSH internal/toolgroups/toolgroups.go。
using System.Reflection;
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;
using SubSsh.Core.ToolGroups;

namespace SubSsh.Plugin.ToolGroups;

[Plugin("tool-groups", "工具组常量与目录（分组名是 disabled_tools 的唯一取值来源；可被同接口插件替换）")]
public sealed class ToolGroupsPlugin : IComponent
{
    public string Name => "tool-groups";

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        var service = new ToolGroupsService();
        ctx.Provide<IToolGroups>(service);
        WireDbHook(ctx);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 把 SubSsh.Plugin.Db.SqliteDb.NormalizeDisabledToolsHook 接到 NormalizeList
    /// （对应 Go encodeDisabledTools 调 toolgroups.NormalizeList）。
    /// 插件之间不互相引用，这里按已加载程序集反射定位 hook；Db 插件不存在/被替换时静默跳过。
    /// </summary>
    private static void WireDbHook(IContext ctx)
    {
        var property = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("SubSsh.Plugin.Db.SqliteDb", throwOnError: false))
            .FirstOrDefault(t => t != null)
            ?.GetProperty("NormalizeDisabledToolsHook", BindingFlags.Public | BindingFlags.Static);
        if (property == null || property.PropertyType != typeof(Func<string[]?, string[]?>))
        {
            return;
        }

        property.SetValue(null, (Func<string[]?, string[]?>)(names => ToolGroupsService.NormalizeList(names ?? [])));
        // 时间可组合：卸载时撤销 hook，避免指向已回收的实现。
        ctx.Track(
            apply: () => { },
            revert: () => property.SetValue(null, null));
    }
}

/// <summary>IToolGroups 实现：全部逻辑包装 Core 的 ToolGroupCatalog（常量单一来源）。</summary>
public sealed class ToolGroupsService : IToolGroups
{
    public IReadOnlyList<ToolGroupDefinition> All => ToolGroupCatalog.All;

    /// <summary>对应 Go toolgroups.Parse：逗号分隔的分组名，未知分组报错。</summary>
    public Task<DisabledGroups> ParseAsync(string spec, CancellationToken ct = default)
    {
        var names = spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return ParseListAsync(names, ct);
    }

    /// <summary>对应 Go toolgroups.ParseList：分组名数组，未知分组报错。</summary>
    public Task<DisabledGroups> ParseListAsync(IReadOnlyList<string> groupNames, CancellationToken ct = default)
    {
        var normalized = NormalizeList(groupNames);
        return Task.FromResult(new DisabledGroups(normalized));
    }

    /// <summary>对应 Go toolgroups.NormalizeList：校验分组名（fail-closed）并按 All 顺序去重。</summary>
    public Task<IReadOnlyList<string>> NormalizeListAsync(IReadOnlyList<string> groupNames, CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<string>)NormalizeList(groupNames));

    /// <summary>对应 Go toolgroups.Union：多份禁用集合取并集。</summary>
    public DisabledGroups Union(params IReadOnlyList<DisabledGroups> parts) => DisabledGroups.Union(parts);

    /// <summary>对应 Go toolgroups.GroupNames。</summary>
    public IReadOnlyList<string> GroupNames() => ToolGroupCatalog.GroupNames();

    /// <summary>对应 Go toolgroups.AllDisabled：解析失败时的 fail-closed 回退。</summary>
    public DisabledGroups AllDisabled() => new(ToolGroupCatalog.GroupNames());

    /// <summary>
    /// 校验分组名并按 All 顺序去重（与 Go toolgroups.NormalizeList 一致）。
    /// 未知分组抛 SubSshException（文案与原版对齐），绝不静默忽略（fail-closed）。
    /// </summary>
    internal static string[] NormalizeList(IReadOnlyList<string> groupNames)
    {
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in groupNames)
        {
            var name = raw.Trim().ToLowerInvariant();
            if (name.Length == 0) continue;
            var (definition, found) = ToolGroupCatalog.Lookup(name);
            if (!found || definition == null)
            {
                throw new SubSshException(
                    $"未知工具组 \"{name}\"；可选值：{string.Join("、", ToolGroupCatalog.GroupNames())}");
            }
            disabled.Add(definition.Group);
        }
        var ordered = new List<string>(disabled.Count);
        foreach (var def in ToolGroupCatalog.All)
            if (disabled.Contains(def.Group)) ordered.Add(def.Group);
        return [.. ordered];
    }
}
