// 对应原版 internal/toolgroups/toolgroups.go：/tool-groups 路由与令牌 disabled_tools 校验的唯一来源。
namespace SubSsh.Plugin.WebApi;

/// <summary>工具组定义（All 的顺序决定错误提示与展示次序，与 Go 版完全一致）。</summary>
public sealed record ToolGroupDef(string Group, string[] Tools);

public static class ToolGroups
{
    public static readonly ToolGroupDef[] All =
    [
        new("hosts", ["hosts_list", "hosts_manage_list", "host_create", "host_update", "host_test", "host_reset_fingerprint", "host_delete"]),
        new("exec", ["exec", "session_env", "output_read"]),
        new("fanout", ["exec_many"]),
        new("jobs", ["job_start", "job_list", "job_status", "job_logs", "job_kill"]),
        new("files", ["file_read", "file_write", "file_edit", "file_list", "file_transfer"]),
        new("search", ["grep", "find"]),
        new("monitor", ["host_status"]),
        new("image", ["image_view"]),
        new("memory", ["memory_remember", "memory_recall", "memory_list", "memory_update", "memory_forget", "memory_stats", "memory_sleep"]),
    ];

    private static bool Lookup(string name) => All.Any(g => string.Equals(g.Group, name, StringComparison.Ordinal));

    private static string Names() => string.Join("、", All.Select(g => g.Group));

    /// <summary>对齐 toolgroups.ParseList：未知分组直接报错，避免静默忽略。</summary>
    private static HashSet<string> ParseList(IEnumerable<string> groupNames)
    {
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in groupNames)
        {
            var name = field.Trim().ToLowerInvariant();
            if (name.Length == 0) continue;
            if (!Lookup(name))
                throw new SubSsh.Core.Abstractions.SubSshException($"未知工具组 \"{name}\"；可选值：{Names()}");
            disabled.Add(name);
        }
        return disabled;
    }

    /// <summary>对齐 toolgroups.NormalizeList：校验并按 All 顺序去重，保持稳定序列化。</summary>
    public static string[] NormalizeList(string[] groupNames)
    {
        var disabled = ParseList(groupNames);
        return All.Where(g => disabled.Contains(g.Group)).Select(g => g.Group).ToArray();
    }
}
