namespace SubSsh.Core.ToolGroups;

// 工具组常量与 SubSSH internal/toolgroups/toolgroups.go 完全一致：
// 分组名是 SUBSSH_DISABLED_TOOLS 与令牌级 denylist 的唯一取值来源，
// 工具名表决定「注册哪些工具」和「服务器提示词里能提到哪些工具」。

/// <summary>分组名常量（对应 Go toolgroups.Group 常量）。</summary>
public static class ToolGroup
{
    public const string Hosts = "hosts";
    public const string Exec = "exec";
    public const string Fanout = "fanout";
    public const string Jobs = "jobs";
    public const string Files = "files";
    public const string Search = "search";
    public const string Monitor = "monitor";
    public const string Image = "image";
    public const string Memory = "memory";
}

/// <summary>工具名常量（与 Go toolgroups.All 中各分组的工具表逐一对应）。</summary>
public static class ToolNames
{
    // hosts
    public const string HostsList = "hosts_list";
    public const string HostsManageList = "hosts_manage_list";
    public const string HostCreate = "host_create";
    public const string HostUpdate = "host_update";
    public const string HostTest = "host_test";
    public const string HostResetFingerprint = "host_reset_fingerprint";
    public const string HostDelete = "host_delete";

    // exec
    public const string Exec = "exec";
    public const string SessionEnv = "session_env";
    public const string OutputRead = "output_read";

    // fanout
    public const string ExecMany = "exec_many";

    // jobs
    public const string JobStart = "job_start";
    public const string JobList = "job_list";
    public const string JobStatus = "job_status";
    public const string JobLogs = "job_logs";
    public const string JobKill = "job_kill";

    // files
    public const string FileRead = "file_read";
    public const string FileWrite = "file_write";
    public const string FileEdit = "file_edit";
    public const string FileList = "file_list";
    public const string FileTransfer = "file_transfer";

    // search
    public const string Grep = "grep";
    public const string Find = "find";

    // monitor
    public const string HostStatus = "host_status";

    // image
    public const string ImageView = "image_view";

    // memory
    public const string MemoryRemember = "memory_remember";
    public const string MemoryRecall = "memory_recall";
    public const string MemoryList = "memory_list";
    public const string MemoryUpdate = "memory_update";
    public const string MemoryForget = "memory_forget";
    public const string MemoryStats = "memory_stats";
    public const string MemorySleep = "memory_sleep";
}

/// <summary>
/// 分组与工具绑定目录（对应 Go toolgroups.All；顺序决定错误提示与文档中的展示次序）。
/// </summary>
public static class ToolGroupCatalog
{
    public static readonly SubSsh.Core.Abstractions.ToolGroupDefinition[] All =
    [
        new(ToolGroup.Hosts,
        [
            ToolNames.HostsList, ToolNames.HostsManageList, ToolNames.HostCreate, ToolNames.HostUpdate,
            ToolNames.HostTest, ToolNames.HostResetFingerprint, ToolNames.HostDelete,
        ]),
        new(ToolGroup.Exec, [ToolNames.Exec, ToolNames.SessionEnv, ToolNames.OutputRead]),
        new(ToolGroup.Fanout, [ToolNames.ExecMany]),
        new(ToolGroup.Jobs,
        [
            ToolNames.JobStart, ToolNames.JobList, ToolNames.JobStatus, ToolNames.JobLogs, ToolNames.JobKill,
        ]),
        new(ToolGroup.Files,
        [
            ToolNames.FileRead, ToolNames.FileWrite, ToolNames.FileEdit, ToolNames.FileList, ToolNames.FileTransfer,
        ]),
        new(ToolGroup.Search, [ToolNames.Grep, ToolNames.Find]),
        new(ToolGroup.Monitor, [ToolNames.HostStatus]),
        new(ToolGroup.Image, [ToolNames.ImageView]),
        new(ToolGroup.Memory,
        [
            ToolNames.MemoryRemember, ToolNames.MemoryRecall, ToolNames.MemoryList, ToolNames.MemoryUpdate,
            ToolNames.MemoryForget, ToolNames.MemoryStats, ToolNames.MemorySleep,
        ]),
    ];

    /// <summary>按 All 顺序返回全部分组名（对应 Go toolgroups.names/GroupNames）。</summary>
    public static IReadOnlyList<string> GroupNames()
    {
        var names = new List<string>(All.Length);
        foreach (var def in All) names.Add(def.Group);
        return names;
    }

    /// <summary>按分组名查工具表（对应 Go toolgroups.lookup）。</summary>
    public static (SubSsh.Core.Abstractions.ToolGroupDefinition? Definition, bool Found) Lookup(string name)
    {
        foreach (var def in All)
            if (string.Equals(def.Group, name, StringComparison.Ordinal))
                return (def, true);
        return (null, false);
    }
}
