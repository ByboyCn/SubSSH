// 工具组定义与禁用列表规范化（对应原版 internal/toolgroups/toolgroups.go）。
// 仅保留 OAuth 授权页 / 授权码存储所需的子集：All、NormalizeList。
namespace SubSsh.Plugin.OAuth;

internal static class ToolGroups
{
    internal sealed record Definition(string Group, string[] Tools);

    // All 的顺序决定错误提示与文档中的展示次序（与原版一致）。
    internal static readonly Definition[] All =
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

    private static readonly Dictionary<string, Definition> Lookup =
        All.ToDictionary(d => d.Group, StringComparer.Ordinal);

    /// <summary>校验分组名并把结果按 All 顺序去重（对应 NormalizeList）。
    /// 未知分组直接报错而不是静默忽略。</summary>
    internal static List<string> NormalizeList(IEnumerable<string> groupNames)
    {
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in groupNames)
        {
            var name = raw.Trim().ToLowerInvariant();
            if (name.Length == 0) continue;
            if (!Lookup.ContainsKey(name))
                throw new OAuthRequestError("invalid_request",
                    $"未知工具组 \"{name}\"；可选值：{string.Join("、", All.Select(d => d.Group))}");
            enabled.Add(name);
        }
        var out_ = new List<string>();
        foreach (var def in All)
            if (enabled.Contains(def.Group)) out_.Add(def.Group);
        return out_;
    }
}
