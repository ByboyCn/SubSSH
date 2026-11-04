// SubSsh.Plugin.McpStdio — MCP stdio 传输插件（可插拔、可任意替换）。
// GPL-3.0。宿主以 --stdio 参数启动时接管 stdin/stdout，逐行收发 JSON-RPC 2.0：
// 响应单行 JSON 写 stdout（协议通道必须干净）；通知/日志一律写 stderr。
// 非 stdio 模式静默不启用（不 Require IMcpServer，不占用控制台）。
// 协议分发复用 IMcpServer.HandleMessageAsync（Provide: Plugin.McpCore），本插件无新接口。
using System.Text;
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;

namespace SubSsh.Plugin.McpStdio;

[Plugin("mcp-stdio", "MCP stdio 传输：宿主以 --stdio 启动时在 stdin/stdout 逐行收发 JSON-RPC（响应走 stdout，日志/通知走 stderr）")]
public sealed class McpStdioPlugin : IComponent
{
    public string Name => "mcp-stdio";

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        // 只有宿主以 --stdio 启动才接管控制台；普通交互模式下宿主 ConsoleLoop 占用 stdin，静默不启用。
        if (!Environment.GetCommandLineArgs().Contains("--stdio", StringComparer.Ordinal))
            return Task.CompletedTask;

        var cts = new CancellationTokenSource();
        Task? pump = null;

        ctx.Require<IMcpServer>(
            onAdd: server =>
            {
                if (pump != null) return; // IMcpServer 提供者更替时旧泵仍在：等卸载回滚后再重建
                pump = Task.Run(async () =>
                {
                    // 等宿主全量装载完成（SignalLoadComplete）再收发请求，避免 tools/list 早于工具包注册
                    await PluginManager.WaitForLoadCompletionAsync(cts.Token);
                    await RunPumpAsync(server, cts.Token);
                }, cts.Token);
                // 时间可组合：泵的生命周期登记为可撤销效果，插件卸载按 LIFO 回滚（等待泵收尾）
                ctx.Track(
                    apply: () => { },
                    revert: () => StopPump(cts, ref pump));
            },
            onRemove: () => StopPump(cts, ref pump));
        return Task.CompletedTask;
    }

    private static void StopPump(CancellationTokenSource cts, ref Task? pump)
    {
        cts.Cancel();
        try { pump?.Wait(TimeSpan.FromSeconds(5)); } catch { /* 泵收尾期间的异常不影响卸载 */ }
        pump = null;
    }

    /// <summary>stdio 泵：逐行读 stdin → IMcpServer 分发 → 单行 JSON 写 stdout。</summary>
    private static async Task RunPumpAsync(IMcpServer server, CancellationToken ct)
    {
        await using var stdout = Console.OpenStandardOutput();
        using var writer = new StreamWriter(stdout, new UTF8Encoding(false));
        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        Console.Error.WriteLine($"[mcp-stdio] 已接管 stdin/stdout，MCP 服务器 {server.Name} v{server.Version} 就绪（每行一条 JSON-RPC）");
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break; // stdin EOF：传输结束（宿主 stdio 分支继续常驻，等待 :quit 语义的进程退出）
            line = line.Trim();
            if (line.Length == 0) continue;
            string response;
            try
            {
                // stdio 传输为本地可信通道，principal 为空（无令牌 / 无令牌级禁用组）
                response = await server.HandleMessageAsync(line, principal: null, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[mcp-stdio] 消息处理失败: " + ex.Message);
                continue;
            }
            if (response.Length == 0) continue; // notifications/*：不回包
            await writer.WriteLineAsync(response).ConfigureAwait(false); // 单行 JSON，协议通道
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
        Console.Error.WriteLine("[mcp-stdio] stdin 已关闭，stdio 传输退出");
    }
}
