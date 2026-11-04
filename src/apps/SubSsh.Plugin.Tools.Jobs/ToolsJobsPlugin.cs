// SubSsh.Plugin.Tools.Jobs — 后台任务工具包插件（对应 Go mcpserver/jobs.go 全部工具）。
// Require: IMcpServer + IToolRegistry（注册工具）+ IJobManager + ISubSshStoreFull + IHostManager（按名解析 host）
//          + IToolGroups（必需）；IDenylist（可选，令牌级兜底拦截）。
// 空间可组合：必需提供者齐备才注册工具，任一下线即撤回本插件引用并静默等待；
// 时间可组合：每次注册经 ctx.Track 登记（MCP 侧卸载时注册表随提供者一起消失）。
using System.Text.Json;
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;
using SubSsh.Core.ToolGroups;

namespace SubSsh.Plugin.Tools.Jobs;

[Plugin("tools-jobs", "后台任务工具包插件：job_start/job_list/job_status/job_logs/job_kill（对应 Go mcpserver/jobs.go，name/description/inputSchema 逐字段照抄）")]
public sealed class ToolsJobsPlugin : IComponent
{
    public string Name => "tools-jobs";

    private IContext? _ctx;
    private IToolRegistry? _registry;
    private IJobManager? _jobs;
    private ISubSshStoreFull? _store;
    private IHostManager? _manager;
    private IToolGroups? _groups;
    private IDenylist? _denylist;

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        _ctx = ctx;

        // 必需依赖齐备才注册工具；任一下线即清空引用（注册表随 McpCore 卸载一起消失，静默等待回归）
        void Publish()
        {
            if (_registry == null || _jobs == null || _store == null || _manager == null || _groups == null) return;
            // IHostManager 为必需门槛依赖（按名解析 host 的授权链路；名字解析实际走 store，见 JobsTools.Access）
            var tools = new JobsTools(_store, _jobs, _denylist);
            RegisterTools(_ctx!, _registry, tools);
        }

        void DropRegistry() { _registry = null; }
        void DropJobs() { _jobs = null; }
        void DropStore() { _store = null; }
        void DropManager() { _manager = null; }
        void DropGroups() { _groups = null; }

        ctx.Require<IMcpServer>(_ => Publish(), DropRegistry);
        ctx.Require<IToolRegistry>(r => { _registry = r; Publish(); }, DropRegistry);
        ctx.Require<IJobManager>(j => { _jobs = j; Publish(); }, DropJobs);
        ctx.Require<ISubSshStoreFull>(s => { _store = s; Publish(); }, DropStore);
        ctx.Require<IHostManager>(m => { _manager = m; Publish(); }, DropManager);
        ctx.Require<IToolGroups>(g => { _groups = g; Publish(); }, DropGroups);

        // 可选依赖：令牌级 denylist 兜底
        ctx.Require<IDenylist>(
            d => _denylist = d,
            () => _denylist = null);

        return Task.CompletedTask;
    }

    private static void RegisterTools(IContext ctx, IToolRegistry registry, JobsTools tools)
    {
        // 工具 name/description/inputSchema 逐字段照抄 Go mcpserver/jobs.go
        Register(ctx, registry, ToolNames.JobStart,
            "在远程主机用 setsid + nohup 启动后台任务，立即返回 job_id 与 PID，SSH 会话断开也不会中断。" +
            "适合部署、构建、迁移、抓包这类超过 exec 600 秒上限或需要长期运行的工作；几十秒内能跑完的命令直接用 exec。" +
            "输出合并写入远端 ~/.subssh/jobs/<job_id>/out.log，用 job_logs 读取、job_status 判断结束与退出码、job_kill 终止；日志文件不会自动清理。",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    host = new { type = "string", description = "SSH 主机名，取自 hosts_list" },
                    command = new { type = "string", description = "要在后台运行的 shell 命令；stdout 与 stderr 合并写入任务日志，stdin 为 /dev/null" },
                    cwd = new { type = "string", description = "工作目录，默认 ~；目录不存在任务会以退出码 97 失败" },
                    env = new { type = "object", additionalProperties = new { type = "string" }, description = "额外导出的环境变量；不会读取 session_env 设置的会话环境" },
                },
                required = new[] { "host", "command" },
            }), tools.JobStartAsync);

        Register(ctx, registry, ToolNames.JobList,
            "列出当前令牌启动过的后台任务，按主机并发登录刷新运行中任务的状态，可用 host 过滤。看不到其他令牌的任务。" +
            "刷新整体最多等 8 秒：连不上或超时的任务返回数据库里的上次状态并带 refresh_error，这时 status 只是旧快照，需要确认时稍后再查或用 job_status。",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    host = new { type = "string", description = "只列出该主机的任务，留空表示全部主机" },
                },
            }), tools.JobListAsync);

        Register(ctx, registry, ToolNames.JobStatus,
            "登录主机刷新并返回单个后台任务的状态：running 表示仍在运行，exited 带 exit_code，lost 表示网关已无法确认进程（多为主机重启或进程被外部杀掉，也包括连续 7 天无法登录主机确认），之后不再跟踪。" +
            "log_bytes 是当前日志字节数，可作为 job_logs 的 offset_bytes 基准做增量读取。轮询长任务时优先查状态，不要反复拉全量日志。",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    job_id = new { type = "string", description = "job_start 返回的任务 ID" },
                },
                required = new[] { "job_id" },
            }), tools.JobStatusAsync);

        Register(ctx, registry, ToolNames.JobLogs,
            "读取后台任务的合并输出日志。默认返回末尾 100 行；grep 传扩展正则先过滤再取尾部；offset_bytes 用于从上次读到的位置继续增量读取，适合边跑边看。",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    job_id = new { type = "string", description = "job_start 返回的任务 ID" },
                    tail_lines = new { type = "integer", description = "返回末尾多少行，默认 100，上限 5000" },
                    grep = new { type = "string", description = "扩展正则（grep -E），先过滤再取末尾 tail_lines 行" },
                    offset_bytes = new { type = "integer", description = "从日志的第几个字节开始读，字节序号从 1 起；大于 0 时忽略 grep 与 tail_lines。续读上次已读到 N 字节时传 N+1" },
                },
                required = new[] { "job_id" },
            }), tools.JobLogsAsync);

        Register(ctx, registry, ToolNames.JobKill,
            "终止后台任务：用 setsid 启动的任务按进程组终止，能一并带走子进程。先用默认的 TERM 让进程有机会清理，确认无效再用 KILL 强杀。任务已结束时调用无副作用。",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    job_id = new { type = "string", description = "job_start 返回的任务 ID" },
                    signal = new { type = "string", description = "信号名，仅支持 TERM（默认，优雅退出）或 KILL（强制）" },
                },
                required = new[] { "job_id" },
            }), tools.JobKillAsync);
    }

    private static void Register(IContext ctx, IToolRegistry registry, string name, string description,
        JsonElement schema, McpToolHandler handler) =>
        // 时间可组合：注册登记为可撤销效果（注册表随 McpCore 卸载整体消失，revert 留空）
        ctx.Track(
            () => registry.RegisterAsync(new McpToolDescriptor(name, description, schema), handler),
            () => Task.CompletedTask);

    private static JsonElement Schema(object shape) =>
        JsonSerializer.SerializeToElement(shape, ToolJson.EmitNull);
}
