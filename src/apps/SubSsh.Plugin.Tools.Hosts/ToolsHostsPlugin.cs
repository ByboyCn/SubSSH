// SubSsh.Plugin.Tools.Hosts — 主机工具包插件（对应 Go mcpserver/hosts.go 全部工具）。
// Require: IMcpServer + IToolRegistry（注册工具）+ IHostManager + IOneSshStoreFull + IToolGroups（必需）；
//          IConnectionPool（可选，hosts_list/hosts_manage_list 的 online 状态）；IDenylist（可选，令牌级兜底拦截）。
// 空间可组合：必需提供者齐备才注册工具，任一下线即撤回本插件引用并静默等待；
// 时间可组合：每次注册经 ctx.Track 登记（MCP 侧卸载时注册表随提供者一起消失）。
using System.Text.Json;
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;
using SubSsh.Core.ToolGroups;

namespace SubSsh.Plugin.Tools.Hosts;

[Plugin("tools-hosts", "主机工具包插件：hosts_list/hosts_manage_list/host_create/host_update/host_test/host_reset_fingerprint/host_delete（对应 Go mcpserver/hosts.go，name/description/inputSchema 逐字段照抄）")]
public sealed class ToolsHostsPlugin : IComponent
{
    public string Name => "tools-hosts";

    private IContext? _ctx;
    private IToolRegistry? _registry;
    private IOneSshStoreFull? _store;
    private IHostManager? _manager;
    private IToolGroups? _groups;
    private IConnectionPool? _pool;
    private IDenylist? _denylist;

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        _ctx = ctx;

        // 必需依赖齐备才注册工具；任一下线即清空引用（注册表随 McpCore 卸载一起消失，静默等待回归）
        void Publish()
        {
            if (_registry == null || _store == null || _manager == null || _groups == null) return;
            var tools = new HostsTools(_store, _manager, _pool, _denylist);
            RegisterTools(_ctx!, _registry, tools);
        }

        void DropRegistry() { _registry = null; }
        void DropStore() { _store = null; }
        void DropManager() { _manager = null; }
        void DropGroups() { _groups = null; }

        ctx.Require<IMcpServer>(_ => Publish(), DropRegistry);
        ctx.Require<IToolRegistry>(r => { _registry = r; Publish(); }, DropRegistry);
        ctx.Require<IHostManager>(m => { _manager = m; Publish(); }, DropManager);
        ctx.Require<IOneSshStoreFull>(s => { _store = s; Publish(); }, DropStore);
        ctx.Require<IToolGroups>(g => { _groups = g; Publish(); }, DropGroups);

        // 可选依赖：在线状态与令牌级 denylist，可用即接入，下线仅降级
        ctx.Require<IConnectionPool>(
            p => _pool = p,
            () => _pool = null);
        ctx.Require<IDenylist>(
            d => _denylist = d,
            () => _denylist = null);

        return Task.CompletedTask;
    }

    private static void RegisterTools(IContext ctx, IToolRegistry registry, HostsTools tools)
    {
        // 工具 name/description/inputSchema 逐字段照抄 Go mcpserver/hosts.go
        Register(ctx, registry, ToolNames.HostsList,
            "列出当前令牌可访问的 SSH 主机名、地址、登录用户、标签和实时连接状态。其他工具的 host 参数只接受这里返回的 name，开始任何主机相关任务前先调用一次。可按 tags 区分环境或用途后再选择目标主机。",
            Schema(new { type = "object" }), tools.HostsListAsync);

        Register(ctx, registry, ToolNames.HostsManageList,
            "列出网关内全部 SSH 主机的完整配置：端口、认证方式、密钥 ID、已固定的 TOFU 指纹、监控开关、归档状态与连接状态。用于主机运维，不代表当前令牌有执行权限；执行类工具的可用主机以 hosts_list 为准（已归档主机不会出现在其中）。需要 manage_hosts 权限。",
            Schema(new { type = "object" }), tools.HostsManageListAsync);

        Register(ctx, registry, ToolNames.HostCreate,
            "新增一台 SSH 主机配置。auth_type=key 必须给 key_id，auth_type=password 必须给 password；port 省略按 22 处理，name 必须唯一。只写入配置、不建立连接，创建后用 host_test 验证凭据并固定主机公钥指纹。新主机不会自动加入当前令牌的授权列表。可选 jump_host 指定跳板主机名，连接经该主机中转，支持最多 5 级链但不允许成环。需要 manage_hosts 权限。",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    name = new { type = "string", description = "主机名，网关内唯一，后续所有工具用它引用这台主机" },
                    addr = new { type = "string", description = "主机地址或 IP" },
                    port = new { type = "integer", description = "SSH 端口，省略或 0 表示 22" },
                    username = new { type = "string", description = "SSH 登录用户名" },
                    auth_type = new { type = "string", description = "认证方式：key 或 password" },
                    key_id = new { type = "integer", description = "key 认证使用的密钥 ID，auth_type=key 时必填" },
                    password = new { type = "string", description = "登录密码；auth_type 为 password 时必填，只写不可读，审计中固定脱敏" },
                    jump_host = new { type = "string", description = "跳板主机名（可选）：链路最多 5 级且不能成环" },
                    monitor_enabled = new { type = "boolean", description = "是否纳入后台资源监控轮询，新建时默认开启" },
                    tags = new { type = "array", items = new { type = "string" }, description = "主机标签，自动去空白去重，单个最长 32 字符，最多 16 个" },
                },
                required = new[] { "name", "addr", "port", "username", "auth_type" },
            }), tools.HostCreateAsync);

        Register(ctx, registry, ToolNames.HostUpdate,
            "整体替换一台 SSH 主机的配置：host 指定当前名称，其余字段是替换后的完整值，未提供的字段按空值处理，因此改一个字段也要把其他字段一并回填（可先用 hosts_manage_list 取当前值）。name 与 host 不同即完成改名，主机记忆与授权按 ID 保留。jump_host 同样按整体替换处理，省略即改回直连。需要 manage_hosts 权限。",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    host = new { type = "string", description = "要修改的主机当前名称" },
                    name = new { type = "string", description = "修改后的主机名，与当前名称相同表示不改名" },
                    addr = new { type = "string", description = "主机地址或 IP" },
                    port = new { type = "integer", description = "SSH 端口，0 视为 22" },
                    username = new { type = "string", description = "SSH 登录用户名" },
                    auth_type = new { type = "string", description = "认证方式：key 或 password" },
                    key_id = new { type = "integer", description = "key 认证使用的密钥 ID，auth_type=key 时必填" },
                    password = new { type = "string", description = "新的登录密码；auth_type 保持 password 且沿用原密码时可省略" },
                    jump_host = new { type = "string", description = "跳板主机名，留空表示直连；整体替换语义下省略即清除原跳板配置" },
                    monitor_enabled = new { type = "boolean", description = "是否纳入后台资源监控轮询，省略表示保持原值" },
                    tags = new { type = "array", items = new { type = "string" }, description = "主机标签，用于分组与筛选；整体替换语义下省略即清空标签" },
                },
                required = new[] { "host", "name", "addr", "port", "username", "auth_type" },
            }), tools.HostUpdateAsync);

        Register(ctx, registry, ToolNames.HostTest,
            "连接指定主机并执行只读的 uptime，验证网络、凭据与账号可用性，返回命令输出与退出码。首次连接会记录并固定该主机的公钥指纹（TOFU），之后指纹变化会导致连接被拒。可用于 hosts_list 之外的任意已配置主机。需要 manage_hosts 权限。",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    host = new { type = "string", description = "SSH 主机名，取自 hosts_manage_list 的 name" },
                },
                required = new[] { "host" },
            }), tools.HostTestAsync);

        Register(ctx, registry, ToolNames.HostResetFingerprint,
            "清除主机已固定的 TOFU 公钥指纹，下次连接重新固定。仅在确认指纹变化来自重装系统、更换主机密钥等可信变更时使用；无法解释的指纹变化可能是中间人攻击，应先排查再重置。需要 manage_hosts 权限。",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    host = new { type = "string", description = "SSH 主机名，取自 hosts_manage_list 的 name" },
                },
                required = new[] { "host" },
            }), tools.HostResetFingerprintAsync);

        Register(ctx, registry, ToolNames.HostDelete,
            "删除主机配置及其令牌授权、持久会话、后台任务记录、监控指标和该主机 bank 的全部记忆，不可撤销。被其他主机用作跳板的主机不能删除，需先解除依赖。仅在主机确实下线时使用；只是临时不用请改为关闭 monitor_enabled。需要 manage_hosts 权限。",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    host = new { type = "string", description = "SSH 主机名，取自 hosts_manage_list 的 name" },
                },
                required = new[] { "host" },
            }), tools.HostDeleteAsync);
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
