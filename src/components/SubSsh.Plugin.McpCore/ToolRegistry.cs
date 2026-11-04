// 工具注册表（对应 mcpserver 的 register/AddTool 面；工具包插件经 IToolRegistry 注册）。
// 注册本身在插件宿主侧需走 ctx.Track 登记，卸载按 LIFO 撤销；本实现只维护内存表。
using System.Collections.Concurrent;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.McpCore;

public sealed record RegisteredTool(McpToolDescriptor Descriptor, McpToolHandler Handler);

public sealed class ToolRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<string, RegisteredTool> _tools = new(StringComparer.Ordinal);

    public Task RegisterAsync(McpToolDescriptor tool, McpToolHandler handler, CancellationToken ct = default)
    {
        _tools[tool.Name] = new RegisteredTool(tool, handler);
        return Task.CompletedTask;
    }

    /// <summary>tools/list；disabled 非空时按组过滤（对应 filterTools）。</summary>
    public Task<IReadOnlyList<McpToolDescriptor>> ListAsync(DisabledGroups? disabled, CancellationToken ct = default)
    {
        IReadOnlyList<McpToolDescriptor> list = _tools.Values
            .Where(t => disabled == null || !disabled.HidesTool(t.Descriptor.Name))
            .Select(t => t.Descriptor)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<bool> IsRegisteredAsync(string toolName, CancellationToken ct = default) =>
        Task.FromResult(_tools.ContainsKey(toolName));

    public bool TryGet(string toolName, out RegisteredTool tool) =>
        _tools.TryGetValue(toolName, out tool!);
}
