// SubSsh.Plugin.Tools.Files — 文件类工具组插件。
// GPL-3.0，移植自 SubSSH internal/mcpserver/{files,image,search,monitor}.go。
// Require: IMcpServer + IToolRegistry + ISubSshStoreFull（必需）；
// IFileManager / IImageInspector / ISearchEngine / IHostMonitor（可选：对应服务在线才注册其工具）。
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;
using SubSsh.Core.Models;
using SubSsh.Core.ToolGroups;
using HostConfig = SubSsh.Core.Abstractions.HostConfig;

namespace SubSsh.Plugin.Tools.Files;

[Plugin("tools-files", "文件类工具组：file_read/file_write/file_edit/file_list/file_transfer、grep/find、image_view、host_status")]
public sealed class ToolsFilesPlugin : IComponent
{
    public string Name => "tools-files";

    private IToolRegistry? _registry;
    private ISubSshStoreFull? _store;
    private IFileManager? _files;

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        // 必需依赖：注册表与存储；任一下线由 McpCore 卸载管线级联撤回工具（IToolRegistry 无显式反注册 API）
        ctx.Require<IToolRegistry>(r => { _registry = r; }, () => { _registry = null; });
        ctx.Require<ISubSshStoreFull>(r => { _store = r; }, () => { _store = null; });

        // 可选能力：对应服务上线才注册其工具（缺省时不注册）
        ctx.Require<IFileManager>(f => { _files = f; RegisterLater(ctx, () => RegisterFileToolsAsync(f)); }, () => { });
        ctx.Require<IImageInspector>(i => RegisterLater(ctx, () => RegisterImageToolAsync(i)), () => { });
        ctx.Require<ISearchEngine>(s => RegisterLater(ctx, () => RegisterSearchToolsAsync(s)), () => { });
        ctx.Require<IHostMonitor>(m => RegisterLater(ctx, () => RegisterMonitorToolAsync(m)), () => { });

        return Task.CompletedTask;
    }

    /// <summary>时间可组合：注册登记为可撤销效果（revert 由 McpCore 卸载级联完成）。</summary>
    private static void RegisterLater(IContext ctx, Func<Task> register)
        => ctx.Track(apply: () => register().GetAwaiter().GetResult(), revert: () => { });

    private async Task RegisterFileToolsAsync(IFileManager f)
    {
        var registry = _registry;
        if (registry == null) return;

        await Register(registry, ToolNames.FileRead, "读取远程文件",
            "按行读取远程文本文件，返回带行号的内容、全文 SHA-256、字节数和总行数。" +
            "文件上限 4MiB，检测到二进制内容会拒绝（图片改用 image_view）。默认从第 1 行返回 500 行，用 offset/limit 翻页。" +
            "准备用 file_edit 修改前先读一次，把返回的 sha256 作为 expected_sha256 传回去。",
            """
            {
              "type": "object",
              "properties": {
                "host": { "type": "string", "description": "SSH 主机名，取自 hosts_list" },
                "path": { "type": "string", "description": "远程文件路径，支持绝对路径、~ 展开与相对 SFTP 起始目录的路径" },
                "offset": { "type": "integer", "description": "起始行号，从 1 开始，默认 1" },
                "limit": { "type": "integer", "description": "最大返回行数，默认 500，上限 5000" }
              },
              "required": ["host", "path"],
              "additionalProperties": false
            }
            """,
            async (args, context, ct) =>
            {
                var (host, err) = await AuthorizedAsync(context, args, ct).ConfigureAwait(false);
                if (err != null) return err;
                try
                {
                    var out1 = await f.ReadAsync(host, Str(args, "path"), Int(args, "offset"), Int(args, "limit"), ct).ConfigureAwait(false);
                    return Ok(out1);
                }
                catch (Exception ex) { return McpToolResult.Error(ex.Message); }
            }).ConfigureAwait(false);

        await Register(registry, ToolNames.FileWrite, "写入远程文件",
            "把 content 作为完整文件内容写到远程主机：先写同目录临时文件再 rename 覆盖，避免读到写了一半的文件；自动创建父目录，返回写入字节数与 SHA-256。" +
            "这是整文件覆盖，只改局部请用 file_edit，否则会丢掉这次没写进 content 的内容。" +
            "目标文件系统不支持覆盖 rename 时会退化为先删后写，结果里 non_atomic=true 并带 warning。",
            """
            {
              "type": "object",
              "properties": {
                "host": { "type": "string", "description": "SSH 主机名，取自 hosts_list" },
                "path": { "type": "string", "description": "远程文件路径，父目录不存在会自动创建" },
                "content": { "type": "string", "description": "写入的完整文件内容，会覆盖原文件" },
                "mode": { "type": "string", "description": "八进制权限，例如 0644 或 0600，默认 0644" }
              },
              "required": ["host", "path", "content"],
              "additionalProperties": false
            }
            """,
            async (args, context, ct) =>
            {
                var (host, err) = await AuthorizedAsync(context, args, ct).ConfigureAwait(false);
                if (err != null) return err;
                var mode = 420; // 0o644
                var modeText = Str(args, "mode");
                if (modeText.Length > 0)
                {
                    if (!IsOctal(modeText) || !uint.TryParse(modeText, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                        return McpToolResult.Error("mode 必须是八进制权限");
                    mode = unchecked((int)Convert.ToUInt32(modeText, 8));
                }
                var content = args.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                try
                {
                    var out1 = await f.WriteAsync(host, Str(args, "path"), Encoding.UTF8.GetBytes(content), mode, ct).ConfigureAwait(false);
                    return Ok(out1);
                }
                catch (Exception ex) { return McpToolResult.Error(ex.Message); }
            }).ConfigureAwait(false);

        await Register(registry, ToolNames.FileEdit, "结构化编辑远程文件",
            "对远程文本文件做精确替换，是修改配置和代码的首选方式。每个 edit 的 old_text 必须在文件中恰好出现一次，否则整批编辑失败且文件保持原样——匹配不唯一时请把上下文写长一些。" +
            "编辑按数组顺序依次应用，成功后返回统一 diff 和新的 SHA-256。传 expected_sha256（来自 file_read）可开启乐观锁，冲突时应重新 file_read 再重试。" +
            "注意写回后权限固定为 0644，需要其他权限请改用 file_write 并指定 mode。",
            """
            {
              "type": "object",
              "properties": {
                "host": { "type": "string", "description": "SSH 主机名，取自 hosts_list" },
                "path": { "type": "string", "description": "远程文件路径" },
                "edits": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "old_text": { "type": "string", "description": "要被替换的原文，必须在文件中唯一匹配；建议带足上下文以保证唯一" },
                      "new_text": { "type": "string", "description": "替换后的新文本，空字符串表示删除这段内容" }
                    },
                    "required": ["old_text", "new_text"],
                    "additionalProperties": false
                  },
                  "description": "按顺序应用的替换列表；每个 old_text 必须在文件中唯一匹配一次"
                },
                "expected_sha256": { "type": "string", "description": "乐观锁：上一次 file_read 返回的 sha256，文件已变化时报冲突并放弃写入" }
              },
              "required": ["host", "path", "edits"],
              "additionalProperties": false
            }
            """,
            async (args, context, ct) =>
            {
                var (host, err) = await AuthorizedAsync(context, args, ct).ConfigureAwait(false);
                if (err != null) return err;
                var edits = new List<FileEdit>();
                if (args.TryGetProperty("edits", out var e) && e.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in e.EnumerateArray())
                        edits.Add(new FileEdit
                        {
                            OldText = item.TryGetProperty("old_text", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "",
                            NewText = item.TryGetProperty("new_text", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "",
                        });
                }
                try
                {
                    var out1 = await f.EditAsync(host, Str(args, "path"), edits, Str(args, "expected_sha256"), ct).ConfigureAwait(false);
                    return Ok(out1);
                }
                catch (Exception ex) { return McpToolResult.Error(ex.Message); }
            }).ConfigureAwait(false);

        await Register(registry, ToolNames.FileList, "列出远程目录",
            "列出远程目录下的条目：名称、字节数、权限、mtime、是否目录、软链接目标；目录在前，同类按名称排序。" +
            "只列一层，不递归；条目超过 500 会报错，这时请换更精确的目录或改用 find 按 glob 查找。",
            """
            {
              "type": "object",
              "properties": {
                "host": { "type": "string", "description": "SSH 主机名，取自 hosts_list" },
                "path": { "type": "string", "description": "远程目录路径，默认登录用户的起始目录" }
              },
              "required": ["host"],
              "additionalProperties": false
            }
            """,
            async (args, context, ct) =>
            {
                var (host, err) = await AuthorizedAsync(context, args, ct).ConfigureAwait(false);
                if (err != null) return err;
                var path = Str(args, "path");
                if (path.Length == 0) path = ".";
                try
                {
                    var entries = await f.ListAsync(host, path, ct).ConfigureAwait(false);
                    return Ok(new FileListOutput { Entries = entries });
                }
                catch (Exception ex) { return McpToolResult.Error(ex.Message); }
            }).ConfigureAwait(false);

        await Register(registry, ToolNames.FileTransfer, "跨主机复制文件",
            "经网关在两台授权主机之间流式复制文件，两端都用网关自己的凭据，无需主机之间互相打通 SSH。" +
            "返回字节数、源 SHA-256 以及目标校验结果（verified）。目标文件存在会被覆盖，整体超时 10 分钟。比在 exec 里拼 scp/rsync 更可靠。",
            """
            {
              "type": "object",
              "properties": {
                "src_host": { "type": "string", "description": "源主机名，取自 hosts_list" },
                "src_path": { "type": "string", "description": "源文件路径" },
                "dst_host": { "type": "string", "description": "目标主机名，取自 hosts_list；可与源主机相同" },
                "dst_path": { "type": "string", "description": "目标文件路径，已存在会被覆盖" }
              },
              "required": ["src_host", "src_path", "dst_host", "dst_path"],
              "additionalProperties": false
            }
            """,
            async (args, context, ct) =>
            {
                var (src, err1) = await AuthorizedAsync(context, args, ct, hostKey: "src_host").ConfigureAwait(false);
                if (err1 != null) return err1;
                var (dst, err2) = await AuthorizedAsync(context, args, ct, hostKey: "dst_host").ConfigureAwait(false);
                if (err2 != null) return err2;
                try
                {
                    var out1 = await f.TransferAsync(src, Str(args, "src_path"), dst, Str(args, "dst_path"), ct).ConfigureAwait(false);
                    return Ok(out1);
                }
                catch (Exception ex) { return McpToolResult.Error(ex.Message); }
            }).ConfigureAwait(false);
    }

    private async Task RegisterImageToolAsync(IImageInspector inspector)
    {
        var registry = _registry;
        if (registry == null) return;

        await Register(registry, ToolNames.ImageView, "查看远程图片",
            "读取远程 PNG/JPEG/GIF/WebP 图片，按 max_dim 等比缩放后作为图片内容直接返回，另附原始与输出尺寸、字节数、MIME 类型。" +
            "用于看截图、监控图表、渲染产物；file_read 遇到二进制会拒绝，改用这个。原文件上限 20MiB，超大像素图会被拒绝。",
            """
            {
              "type": "object",
              "properties": {
                "host": { "type": "string", "description": "SSH 主机名，取自 hosts_list" },
                "path": { "type": "string", "description": "远程图片路径，原文件不超过 20MiB" },
                "max_dim": { "type": "integer", "description": "长边像素上限，默认 1024，上限 2048；超出会等比缩小" }
              },
              "required": ["host", "path"],
              "additionalProperties": false
            }
            """,
            async (args, context, ct) =>
            {
                var files = _files;
                if (files == null) return McpToolResult.Error("文件组件未上线");
                var (host, err) = await AuthorizedAsync(context, args, ct).ConfigureAwait(false);
                if (err != null) return err;
                byte[] data;
                try { data = await files.RawReadAsync(host, Str(args, "path"), 20 << 20, ct).ConfigureAwait(false); }
                catch (Exception ex) { return McpToolResult.Error(ex.Message); }
                ImageProcessResult result;
                try { result = await inspector.ProcessAsync(data, Int(args, "max_dim"), ct).ConfigureAwait(false); }
                catch (Exception ex) { return McpToolResult.Error(ex.Message); }
                var meta = new ImageOutput
                {
                    OriginalWidth = result.OriginalWidth, OriginalHeight = result.OriginalHeight,
                    Width = result.Width, Height = result.Height,
                    Bytes = result.Data.Length, MimeType = result.MimeType,
                };
                var text = $"原始 {meta.OriginalWidth}x{meta.OriginalHeight}，输出 {meta.Width}x{meta.Height}，{meta.Bytes} 字节";
                // 注：Go 版 content 还包含 mcp.ImageContent（图片本体）；McpToolResult 目前只支持文本内容，
                // 图片字节暂以 meta 文本返回，待 Core 抽象扩展 ImageContent 后补齐。
                return new McpToolResult([text, Json(meta)]);
            }).ConfigureAwait(false);
    }

    private async Task RegisterSearchToolsAsync(ISearchEngine search)
    {
        var registry = _registry;
        if (registry == null) return;

        await Register(registry, ToolNames.Grep, "搜索远程文件内容",
            "在远程主机上按内容搜索文件，返回结构化的路径、行号、列号、匹配行与上下文行，以及是否被 limit 截断。" +
            "这是排查日志和定位配置的首选，比在 exec 里拼 grep 更省心：引号转义、超时、输出上限都已处理。" +
            "远端有 ripgrep 时走原生路径，否则自动降级（engine 字段标明实际路径，降级时带 warning，大目录会更慢）。" +
            "远端缺少 rg 且平台受支持（Linux amd64/arm64）时，会先经 SFTP 上传一个临时静态搜索 helper 在远端本地遍历（engine=helper），运行结束立即删除、不安装任何东西；上传或执行受阻时自动退回纯 SFTP。" +
            "两条降级路径都会跳过二进制、符号链接和超大文件，并遵守目录内的 .gitignore/.ignore/.rgignore。",
            """
            {
              "type": "object",
              "properties": {
                "host": { "type": "string", "description": "SSH 主机名，取自 hosts_list" },
                "pattern": { "type": "string", "description": "搜索模式，默认按正则解释；含特殊字符的字面串请配合 literal=true" },
                "path": { "type": "string", "description": "远程文件或目录，默认登录用户家目录；尽量收窄以提速" },
                "glob": { "type": "string", "description": "只搜索匹配该 glob 的文件，例如 *.go 或 **/*.test.ts" },
                "ignoreCase": { "type": "boolean", "description": "忽略大小写" },
                "literal": { "type": "boolean", "description": "把 pattern 当作字面字符串而不是正则" },
                "context": { "type": "integer", "description": "每个匹配前后附带的上下文行数，最大 20；结果里 match=false 的行即上下文" },
                "limit": { "type": "integer", "description": "最大匹配行数，默认 100，上限 2000；命中上限时 truncated=true" }
              },
              "required": ["host", "pattern"],
              "additionalProperties": false
            }
            """,
            async (args, context, ct) =>
            {
                var (host, err) = await AuthorizedAsync(context, args, ct).ConfigureAwait(false);
                if (err != null) return err;
                try
                {
                    var out1 = await search.GrepAsync(host, new GrepOptions
                    {
                        Pattern = Str(args, "pattern"),
                        Path = Str(args, "path"),
                        Glob = Str(args, "glob"),
                        IgnoreCase = Bool(args, "ignoreCase"),
                        Literal = Bool(args, "literal"),
                        Context = Int(args, "context"),
                        Limit = Int(args, "limit"),
                    }, ct).ConfigureAwait(false);
                    return Ok(out1);
                }
                catch (Exception ex) { return McpToolResult.Error(ex.Message); }
            }).ConfigureAwait(false);

        await Register(registry, ToolNames.Find, "查找远程路径",
            "在远程主机上按 glob 查找文件和目录路径，只返回路径不读内容，适合先定位再用 file_read 或 grep 精查。" +
            "远端有 fd/fdfind 时走原生路径，否则自动降级（engine 字段标明实际路径）。" +
            "远端缺少 fd/fdfind 且平台受支持（Linux amd64/arm64）时，会先经 SFTP 上传一个临时静态搜索 helper 在远端本地遍历（engine=helper），运行结束立即删除、不安装任何东西；上传或执行受阻时自动退回纯 SFTP。要列单层目录用 file_list 更直接。",
            """
            {
              "type": "object",
              "properties": {
                "host": { "type": "string", "description": "SSH 主机名，取自 hosts_list" },
                "pattern": { "type": "string", "description": "路径 glob，例如 *.go、**/*.json 或 src/**/*.spec.ts；不含 / 时按文件名匹配" },
                "path": { "type": "string", "description": "远程搜索起始目录，默认登录用户家目录" },
                "limit": { "type": "integer", "description": "最大结果数，默认 1000，上限 5000" }
              },
              "required": ["host", "pattern"],
              "additionalProperties": false
            }
            """,
            async (args, context, ct) =>
            {
                var (host, err) = await AuthorizedAsync(context, args, ct).ConfigureAwait(false);
                if (err != null) return err;
                try
                {
                    var out1 = await search.FindAsync(host, new FindOptions
                    {
                        Pattern = Str(args, "pattern"),
                        Path = Str(args, "path"),
                        Limit = Int(args, "limit"),
                    }, ct).ConfigureAwait(false);
                    return Ok(out1);
                }
                catch (Exception ex) { return McpToolResult.Error(ex.Message); }
            }).ConfigureAwait(false);
    }

    private async Task RegisterMonitorToolAsync(IHostMonitor monitor)
    {
        var registry = _registry;
        if (registry == null) return;

        await Register(registry, ToolNames.HostStatus, "读取主机资源指标",
            "读取主机的 CPU 使用率、内存用量、1 分钟负载和各挂载点磁盘用量快照。" +
            "默认返回后台轮询存下的最近一条采样，很快但可能有分钟级延迟；需要当前真实状态时传 fresh=true 现场采集。" +
            "主机未开启监控或还没采过样时会提示改用 fresh=true。只看资源用这个，不要用 exec 跑 top/df 自己解析。",
            """
            {
              "type": "object",
              "properties": {
                "host": { "type": "string", "description": "SSH 主机名，取自 hosts_list" },
                "fresh": { "type": "boolean", "description": "true 表示立即登录主机现场采样；默认读取后台轮询的最近一条记录" }
              },
              "required": ["host"],
              "additionalProperties": false
            }
            """,
            async (args, context, ct) =>
            {
                var store = _store;
                if (store == null) return McpToolResult.Error("数据库组件未上线");
                HostConfig h;
                try { h = await AuthorizedHostAsync(store, context, Str(args, "host"), ct).ConfigureAwait(false); }
                catch (SubSshException ex) { return McpToolResult.Error(ex.Message); }
                if (Bool(args, "fresh"))
                {
                    try
                    {
                        var snap = await monitor.FreshAsync(h, ct).ConfigureAwait(false);
                        return Ok(snap);
                    }
                    catch (Exception ex) { return McpToolResult.Error(ex.Message); }
                }
                MetricRecord? metric;
                try { metric = await store.LatestMetricAsync(h.Id, ct).ConfigureAwait(false); }
                catch (Exception) { metric = null; }
                if (metric == null)
                    return McpToolResult.Error("暂无监控数据，可使用 fresh=true");
                return Ok(FromMetric(metric));
            }).ConfigureAwait(false);
    }

    /// <summary>store.Metric → monitor.Snapshot（对应 Go monitor.FromMetric；Plugin.Monitor 内部实现不可跨插件引用，此处同形）。</summary>
    private static MonitorSnapshot FromMetric(MetricRecord m)
    {
        var disks = new List<MonitorDisk>();
        try
        {
            if (!string.IsNullOrEmpty(m.DisksJson))
                disks = JsonSerializer.Deserialize<List<MonitorDisk>>(m.DisksJson) ?? [];
        }
        catch (JsonException) { }

        return new MonitorSnapshot
        {
            HostId = m.HostId,
            Ts = m.Ts,
            CpuPct = m.CpuPct,
            MemUsedKb = m.MemUsedKb,
            MemTotalKb = m.MemTotalKb,
            Load1 = m.Load1,
            Disks = disks,
        };
    }

    // ------------------------------------------------------------------
    // 公共小件
    // ------------------------------------------------------------------
    private async Task<(string Host, McpToolResult? Error)> AuthorizedAsync(
        McpCallContext context, JsonElement args, CancellationToken ct, string hostKey = "host")
    {
        var store = _store;
        if (store == null) return ("", McpToolResult.Error("数据库组件未上线"));
        try
        {
            var h = await AuthorizedHostAsync(store, context, Str(args, hostKey), ct).ConfigureAwait(false);
            return (h.Name, null);
        }
        catch (SubSshException ex) { return ("", McpToolResult.Error(ex.Message)); }
    }

    /// <summary>主机访问授权（对应 Go AuthorizedHost）。
    /// TODO：令牌级主机集合授权（McpPrincipal 在传输层构造，PrincipalFrom 当前恒 null，见 McpCore）。</summary>
    private static async Task<HostConfig> AuthorizedHostAsync(
        ISubSshStoreFull store, McpCallContext context, string name, CancellationToken ct)
    {
        _ = context;
        var found = await store.GetHostByNameAsync(name, ct).ConfigureAwait(false);
        if (found == null) throw new SubSshException("unknown host: " + name);
        if (found.ArchivedAt != null)
            throw new SubSshException("host archived: " + name + "（主机已归档，需管理员在控制台取消归档）");
        return found;
    }

    private static bool IsOctal(string text)
    {
        foreach (var ch in text)
            if (ch is < '0' or > '7') return false;
        return text.Length > 0;
    }

    private static string Str(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static bool Bool(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static McpToolResult Ok<T>(T value) => McpToolResult.Text(Json(value));

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOpts.Default);

    private static async Task Register(
        IToolRegistry registry, string name, string title, string description, string schemaJson,
        Func<JsonElement, McpCallContext, CancellationToken, Task<McpToolResult>> handler)
    {
        _ = title; // Go mcp.Tool.Title 不进入 inputSchema 输出面，这里保留注释语义
        var descriptor = new McpToolDescriptor(name, description, JsonDocument.Parse(schemaJson).RootElement.Clone());
        await registry.RegisterAsync(descriptor, (args, ctx, ct) => handler(args, ctx, ct)).ConfigureAwait(false);
    }

    internal static class JsonOpts
    {
        // 输出字段名与 Go json tag 一致（模型已带 JsonPropertyName；无 tag 的 record 用 snake_case）
        public static readonly JsonSerializerOptions Default = new()
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        };
    }

    private sealed record ImageOutput
    {
        [JsonPropertyName("original_width")] public int OriginalWidth { get; init; }
        [JsonPropertyName("original_height")] public int OriginalHeight { get; init; }
        [JsonPropertyName("width")] public int Width { get; init; }
        [JsonPropertyName("height")] public int Height { get; init; }
        [JsonPropertyName("bytes")] public int Bytes { get; init; }
        [JsonPropertyName("mime_type")] public string MimeType { get; init; } = "";
    }

    private sealed record FileListOutput
    {
        [JsonPropertyName("entries")] public IReadOnlyList<FileEntry> Entries { get; init; } = [];
    }
}
