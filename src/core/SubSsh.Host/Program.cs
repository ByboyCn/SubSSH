// SubSsh.Host — 唯一 exe（宿主）：宿主永远不变，一切皆插件（可插拔、可任意替换）。
// GPL-3.0，移植自 https://github.com/Lynricsy/SubSSH，架构遵循
// 《A Programming Paradigm for Spatiotemporal Composability》(arXiv:2608.25512)。
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;
using SubSsh.Core.Events;

namespace SubSsh.Host;

public static class Program
{
    public const string Version = "dev-skeleton";

    public static async Task<int> Main(string[] args)
    {
        var baseDir = AppContext.BaseDirectory;
        var pluginsDir = Path.Combine(baseDir, "plugins");
        var composition = new Composition();
        var bus = new EventBus();

        // 宿主级设置：读环境变量（SUBSSH_*），作为第一个服务发布
        var settings = HostSettings.FromEnvironment(baseDir);
        composition.Provide<ISettings>("host", settings);

        var manager = new PluginManager(pluginsDir, composition, bus);

        // --stdio 分支：MCP stdio 传输接管 stdin/stdout，宿主跳过控制台命令循环、
        // 启动时自动 :load * 全量加载插件（等价 ImBot 范式的 --ui 子进程分支）。
        // 全部宿主输出走 stderr，保持 stdout 为纯 JSON-RPC 协议通道。
        if (args.Contains("--stdio", StringComparer.Ordinal))
            return await StdioBranch(manager, pluginsDir);

        Console.WriteLine($"SubSsh {Version} 宿主启动，插件目录 {pluginsDir}");
        Console.WriteLine($"目录中共 {manager.Catalog.Count} 个插件，输入 :list 查看，:load * 全量加载");

        // 控制台命令循环（: 前缀，带外管理通道）
        return await ConsoleLoop(manager, composition);
    }

    private static async Task<int> StdioBranch(PluginManager manager, string pluginsDir)
    {
        // stdout 是纯 JSON-RPC 协议通道：宿主与插件（含 ASP.NET 控制台日志）的一切辅助输出
        // 统一改道 stderr；mcp-stdio 泵走 Console.OpenStandardOutput 原始流，不受此影响。
        Console.SetOut(Console.Error);
        Console.Error.WriteLine($"SubSsh {Version} 宿主启动（--stdio，跳过控制台命令循环），插件目录 {pluginsDir}");
        Console.Error.WriteLine($"目录中共 {manager.Catalog.Count} 个插件，自动 :load * 全量加载（单个插件失败不阻断其余）");
        foreach (var id in manager.Catalog.Select(c => c.Id))
        {
            try { Console.Error.WriteLine(await manager.LoadAsync(id)); }
            catch (Exception ex) { Console.Error.WriteLine($"{id}: 加载失败 {ex.Message}"); }
        }
        // 全部装载完成后才放行 stdio 传输（mcp-stdio 泵等待此信号，避免请求先于工具注册）
        PluginManager.SignalLoadComplete();
        // 常驻：协议收发由 mcp-stdio 插件的泵负责，宿主只等待进程信号
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    private static async Task<int> ConsoleLoop(PluginManager manager, Composition composition)
    {
        while (true)
        {
            var line = Console.ReadLine();
            if (line == null)
            {
                // stdin EOF（如被脚本拉起）：保持常驻，等待 :quit 或进程信号
                await Task.Delay(500);
                continue;
            }
            line = line.Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith(':'))
            {
                Console.WriteLine("宿主控制台只接受 : 前缀命令（:list :load :unload :delete :services :quit）");
                continue;
            }
            try
            {
                var parts = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                switch (parts.FirstOrDefault())
                {
                    case "list" or "ls":
                        Console.WriteLine(manager.SnapshotJson());
                        break;
                    case "load":
                        Console.WriteLine(await manager.LoadAsync(parts.Skip(1).FirstOrDefault() ?? "*"));
                        break;
                    case "unload":
                        Console.WriteLine(await manager.UnloadAsync(parts.Skip(1).FirstOrDefault() ?? ""));
                        break;
                    case "delete":
                        Console.WriteLine(await manager.DeleteAsync(parts.Skip(1).FirstOrDefault() ?? ""));
                        break;
                    case "services":
                        foreach (var p in manager.Loaded)
                            Console.WriteLine($"  插件 {p}");
                        foreach (var t in new[] { typeof(ISettings), typeof(ISubSshStore), typeof(IOAuthService), typeof(IWebApiServer) })
                        {
                            var provider = t == typeof(ISettings) ? composition.ProviderOf<ISettings>()
                                : t == typeof(ISubSshStore) ? composition.ProviderOf<ISubSshStore>()
                                : t == typeof(IOAuthService) ? composition.ProviderOf<IOAuthService>()
                                : composition.ProviderOf<IWebApiServer>();
                            Console.WriteLine($"  {t.Name} ← {(provider is null ? "（无提供者）" : provider)}");
                        }
                        break;
                    case "quit" or "exit":
                        foreach (var id in manager.Loaded.ToList())
                            await manager.UnloadAsync(id);
                        return 0;
                    default:
                        Console.WriteLine("未知命令，可用：:list :load :unload :delete :services :quit");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
            }
        }
        return 0;
    }
}

/// <summary>宿主设置：SUBSSH_* 环境变量 + 默认值（对应原版 config.go 语义）。</summary>
public sealed class HostSettings : ISettings
{
    public string DataDir { get; init; } = "";
    public string Listen { get; init; } = "127.0.0.1:8080";
    public string PublicUrl { get; init; } = "";
    public string MasterKey { get; init; } = "";
    public string AdminPassword { get; init; } = "";
    public string EmbeddingApiUrl { get; init; } = "";
    public string EmbeddingApiKey { get; init; } = "";
    public string EmbeddingModel { get; init; } = "";
    public int PollInterval { get; init; } = 30;
    public string SearchHelper { get; init; } = "";
    public bool McpApps { get; init; }
    public string[] DisabledTools { get; init; } = [];

    public static HostSettings FromEnvironment(string baseDir) => new()
    {
        DataDir = Env("SUBSSH_DATA_DIR", Path.Combine(baseDir, "data")),
        Listen = Env("SUBSSH_LISTEN", "127.0.0.1:8080"),
        PublicUrl = Environment.GetEnvironmentVariable("SUBSSH_PUBLIC_URL") ?? "",
        MasterKey = Environment.GetEnvironmentVariable("SUBSSH_MASTER_KEY") ?? "",
        AdminPassword = Environment.GetEnvironmentVariable("SUBSSH_ADMIN_PASSWORD") ?? "",
        EmbeddingApiUrl = Environment.GetEnvironmentVariable("SUBSSH_EMBEDDING_API_URL") ?? "",
        EmbeddingApiKey = Environment.GetEnvironmentVariable("SUBSSH_EMBEDDING_API_KEY") ?? "",
        EmbeddingModel = Environment.GetEnvironmentVariable("SUBSSH_EMBEDDING_MODEL") ?? "",
        PollInterval = int.TryParse(Environment.GetEnvironmentVariable("SUBSSH_POLL_INTERVAL"), out var p) ? p : 30,
        SearchHelper = Environment.GetEnvironmentVariable("SUBSSH_SEARCH_HELPER") ?? "",
        McpApps = Environment.GetEnvironmentVariable("SUBSSH_MCP_APPS") is "1" or "true",
        DisabledTools = (Environment.GetEnvironmentVariable("SUBSSH_DISABLED_TOOLS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    };

    private static string Env(string name, string fallback)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;
}
