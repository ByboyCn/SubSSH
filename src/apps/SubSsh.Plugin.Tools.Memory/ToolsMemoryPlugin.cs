// SubSsh.Plugin.Tools.Memory — 记忆工具包插件（对应 Go mcpserver/memory.go 全部工具）。
// Require: IMcpServer + IToolRegistry（注册工具）+ IMemoryEngine + IOneSshStoreFull + IToolGroups（必需）；
//          IDenylist（可选，令牌级兜底拦截）。
// bank 语义照抄 Go memoryBank：host 参数为空 → 全局 bank；否则按名授权解析到主机 bank。
// 空间可组合：必需提供者齐备才注册工具，任一下线即撤回本插件引用并静默等待；
// 时间可组合：每次注册经 ctx.Track 登记（MCP 侧卸载时注册表随提供者一起消失）。
using System.Text.Json;
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;
using SubSsh.Core.ToolGroups;

namespace SubSsh.Plugin.Tools.Memory;

[Plugin("tools-memory", "记忆工具包插件：memory_remember/memory_recall/memory_list/memory_update/memory_forget/memory_stats/memory_sleep（对应 Go mcpserver/memory.go，name/description/inputSchema 逐字段照抄）")]
public sealed class ToolsMemoryPlugin : IComponent
{
    public string Name => "tools-memory";

    private IContext? _ctx;
    private IToolRegistry? _registry;
    private IMemoryEngine? _engine;
    private IOneSshStoreFull? _store;
    private IToolGroups? _groups;
    private IDenylist? _denylist;

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        _ctx = ctx;

        // 必需依赖齐备才注册工具；任一下线即清空引用（注册表随 McpCore 卸载一起消失，静默等待回归）
        void Publish()
        {
            if (_registry == null || _engine == null || _store == null || _groups == null) return;
            var tools = new MemoryTools(_store, _engine, _denylist);
            RegisterTools(_ctx!, _registry, tools);
        }

        void DropRegistry() { _registry = null; }
        void DropEngine() { _engine = null; }
        void DropStore() { _store = null; }
        void DropGroups() { _groups = null; }

        ctx.Require<IMcpServer>(_ => Publish(), DropRegistry);
        ctx.Require<IToolRegistry>(r => { _registry = r; Publish(); }, DropRegistry);
        ctx.Require<IMemoryEngine>(e => { _engine = e; Publish(); }, DropEngine);
        ctx.Require<IOneSshStoreFull>(s => { _store = s; Publish(); }, DropStore);
        ctx.Require<IToolGroups>(g => { _groups = g; Publish(); }, DropGroups);

        // 可选依赖：令牌级 denylist 兜底
        ctx.Require<IDenylist>(
            d => _denylist = d,
            () => _denylist = null);

        return Task.CompletedTask;
    }

    private static void RegisterTools(IContext ctx, IToolRegistry registry, MemoryTools tools)
    {
        // 工具 name/description/inputSchema 逐字段照抄 Go mcpserver/memory.go
        Register(ctx, registry, ToolNames.MemoryRemember,
            "把一条以后仍然有用的运维结论写进跨会话持久记忆：部署路径、服务拓扑、故障原因与修复手段、环境约束、用户明确的偏好。" +
            "写成简洁自包含的一句话，指定 host 存进该主机 bank，只有真正跨主机通用的规则才留空写全局 bank。" +
            "同一 bank 内正文完全相同会自动去重并返回 deduped=true。" +
            "不要保存密码、私钥、令牌，也不要保存一次性命令输出、未经验证的猜测和低价值重复信息。",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    host = new { type = "string", description = "SSH 主机名，写入该主机 bank；留空写入全局 bank，只放确实跨主机通用的规则" },
                    content = new { type = "string", description = "要长期保存的结论：简洁、自包含、脱离当前对话也能看懂；禁止写入密码、私钥和令牌" },
                    source = new { type = "string", description = "记忆来源标记，默认 mcp" },
                    importance = new { type = "number", description = "重要度 0 到 1，默认 0.5；影响召回排序与长期保留，长期关键约束用 0.8 以上" },
                    veracity = new { type = "string", description = "可信类型：stated 用户明确陈述、inferred 自行推断、tool 工具直接观测、unknown 存疑，默认 stated" },
                },
                required = new[] { "content" },
            }), tools.MemoryRememberAsync);

        Register(ctx, registry, ToolNames.MemoryRecall,
            "按当前问题检索持久记忆。开始一台主机上的实质性工作前先召回一次，避免重复踩坑、重复摸索路径。" +
            "混合打分融合全文检索、重要度、时近度以及可选的语义向量（engine 字段标明实际使用的检索路径）；指定 host 时会同时检索该主机 bank 与全局 bank。" +
            "记忆可能已经过期，只能作为线索，结论仍须用文件、命令输出或监控数据现场验证；没召回到结果就正常调查，不要当成事实不存在。",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    host = new { type = "string", description = "SSH 主机名；指定后同时召回该主机 bank 与全局 bank，留空只召回全局 bank" },
                    query = new { type = "string", description = "当前要解决的具体问题或关键词，越贴近现场问题命中越准" },
                    limit = new { type = "integer", description = "最大结果数，默认 8，上限 50" },
                },
                required = new[] { "query" },
            }), tools.MemoryRecallAsync);

        Register(ctx, registry, ToolNames.MemoryList,
            "按写入时间倒序分页列出某一个 bank 的记忆，用于人工核对与清理，不做相关性排序，也不会把主机 bank 和全局 bank 合并。按问题找内容请用 memory_recall。",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    host = new { type = "string", description = "SSH 主机名，留空列出全局 bank；不会合并全局与主机 bank" },
                    limit = new { type = "integer", description = "最大结果数，默认 50，上限 200" },
                    offset = new { type = "integer", description = "分页偏移量，默认 0" },
                },
            }), tools.MemoryListAsync);

        Register(ctx, registry, ToolNames.MemoryUpdate,
            "修正一条已有记忆的正文、重要度或可信类型。事实发生变化时优先更新原记录，而不是删掉重写或再存一条相似的，避免记忆库里出现互相矛盾的版本。" +
            "正文变化会尽力重建语义向量，失败也不影响更新本身。只能改当前令牌有权访问的记忆。",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "integer", description = "记忆 ID，取自 memory_recall 或 memory_list" },
                    content = new { type = "string", description = "新的记忆正文，省略表示不改；正文变化会重建语义向量" },
                    importance = new { type = "number", description = "新的重要度，0 到 1，省略表示不改" },
                    veracity = new { type = "string", description = "新的可信类型：stated、inferred、tool 或 unknown，省略表示不改" },
                },
                required = new[] { "id" },
            }), tools.MemoryUpdateAsync);

        Register(ctx, registry, ToolNames.MemoryForget,
            "永久删除一条记忆，不可撤销。仅在记录确认错误、已经失效或本就不该保存（例如误存了敏感信息）时使用；只是内容变了请用 memory_update。",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "integer", description = "记忆 ID，取自 memory_recall 或 memory_list" },
                },
                required = new[] { "id" },
            }), tools.MemoryForgetAsync);

        Register(ctx, registry, ToolNames.MemoryStats,
            "统计当前令牌可见的全局 bank 与各主机 bank：记忆条数、已生成向量数、最后写入时间。用于判断某个 bank 是否值得 memory_sleep 整理，或确认记忆是否写进了预期的 bank。",
            Schema(new { type = "object" }), tools.MemoryStatsAsync);

        Register(ctx, registry, ToolNames.MemorySleep,
            "对单个 bank 执行确定性维护，不调用 LLM：合并完全相同的正文、把 30 天未被使用的记忆重要度按 0.9 衰减（下限 0.05）、删除 90 天前且从未被召回、重要度不超过 0.1 的记忆，返回三项的处理条数。" +
            "属于偶尔执行的清理动作，不需要在日常任务里调用；会真实删除低价值记忆，重要内容请先用 memory_update 提高 importance。",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    host = new { type = "string", description = "SSH 主机名，只维护该主机 bank；留空维护全局 bank" },
                },
            }), tools.MemorySleepAsync);
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
