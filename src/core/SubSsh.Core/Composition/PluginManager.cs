using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SubSsh.Core.Composition;

/// <summary>目录中发现的插件元数据（MetadataLoadContext 只读扫描，不锁文件）。</summary>
public sealed record PluginDescriptor(string Id, string Description, string FilePath, string TypeName);

/// <summary>
/// 宿主级管理 API。管理平面与被管理平面分离：不随任何插件卸载，
/// 卸掉 UI/管理插件后仍可用 `:` 命令装回。
/// </summary>
public interface IAdminApi
{
    IReadOnlyList<PluginDescriptor> Catalog { get; }
    IReadOnlyList<string> Loaded { get; }
    Task<string> LoadAsync(string idOrPattern, CancellationToken ct = default);
    Task<string> UnloadAsync(string id, CancellationToken ct = default);
    Task<string> DeleteAsync(string id, CancellationToken ct = default);
}

/// <summary>
/// 插件管理器：发现（只读元数据）与加载（独立 collectible ALC）分离；
/// 卸载按 LIFO 回滚效果并级联卸载消费方。
/// </summary>
public sealed class PluginManager : IAdminApi
{
    private readonly string _pluginsDir;
    private readonly Composition _composition;
    private readonly Core.Events.EventBus _bus;
    private readonly Dictionary<string, LoadedPlugin> _loaded = new();
    private readonly List<PluginDescriptor> _catalog = new();
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public event Action<string>? Changed;

    public PluginManager(string pluginsDir, Composition composition, Core.Events.EventBus bus)
    {
        _pluginsDir = pluginsDir;
        _composition = composition;
        _bus = bus;
        Directory.CreateDirectory(pluginsDir);
        Rescan();
    }

    public IReadOnlyList<PluginDescriptor> Catalog => _catalog;
    public IReadOnlyList<string> Loaded => _loaded.Keys.ToList();

    /// <summary>重新扫描 plugins 目录（不加载）。</summary>
    public void Rescan()
    {
        _catalog.Clear();
        var resolverPaths = new List<string>();
        resolverPaths.AddRange(Directory.GetFiles(AppContext.BaseDirectory, "*.dll"));
        if (Directory.Exists(RuntimeEnvironment.GetRuntimeDirectory()))
            resolverPaths.AddRange(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));
        foreach (var dll in Directory.EnumerateFiles(_pluginsDir, "*.dll"))
        {
            try
            {
                using var mlc = new MetadataLoadContext(new PathAssemblyResolver(resolverPaths.Concat(new[] { dll })));
                foreach (var type in mlc.LoadFromAssemblyPath(dll).GetTypes())
                {
                    // MetadataLoadContext 不能激活特性实例，用 CustomAttributeData 只读解析。
                    var attrData = type.GetCustomAttributesData().FirstOrDefault(a =>
                        a.AttributeType.FullName == "SubSsh.Core.Composition.PluginAttribute");
                    if (attrData != null && type.GetInterfaces()
                            .Any(i => i.FullName == "SubSsh.Core.Composition.IComponent"))
                    {
                        var id = (string)attrData.ConstructorArguments[0].Value!;
                        var description = attrData.ConstructorArguments.Count > 1
                            ? (string)attrData.ConstructorArguments[1].Value! : "";
                        _catalog.Add(new PluginDescriptor(id, description, dll, type.FullName!));
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[catalog] 跳过 {Path.GetFileName(dll)}: {ex.Message}");
            }
        }
        Changed?.Invoke("rescan");
    }

    public async Task<string> LoadAsync(string idOrPattern, CancellationToken ct = default)
    {
        var ids = idOrPattern == "*" ? _catalog.Select(c => c.Id).ToList() : new List<string> { idOrPattern };
        var results = new List<string>();
        foreach (var id in ids)
        {
            var desc = _catalog.FirstOrDefault(c => c.Id == id);
            if (desc == null) { results.Add($"{id}: 目录中不存在"); continue; }
            if (_loaded.ContainsKey(id)) { results.Add($"{id}: 已加载"); continue; }
            await LoadOneAsync(desc, ct);
            results.Add($"{id}: 已加载");
        }
        Changed?.Invoke("load");
        return string.Join('\n', results);
    }

    private async Task LoadOneAsync(PluginDescriptor desc, CancellationToken ct)
    {
        var alc = new AssemblyLoadContext($"plugin:{desc.Id}", isCollectible: true);
        // 插件依赖（如 Microsoft.Data.Sqlite）随插件部署在 plugins\ 目录；
        // 默认解析回退到宿主 Default ALC，找不到时从这里补齐。
        var pluginDir = Path.GetDirectoryName(desc.FilePath)!;
        alc.Resolving += (context, name) =>
        {
            var candidate = Path.Combine(pluginDir, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
        var assembly = alc.LoadFromAssemblyPath(desc.FilePath);
        var type = assembly.GetType(desc.TypeName) ?? throw new InvalidOperationException($"类型丢失: {desc.TypeName}");
        var component = (IComponent)Activator.CreateInstance(type)!;
        var ctx = new PluginContext(desc.Id, _composition, _bus);
        _loaded[desc.Id] = new LoadedPlugin(desc.Id, alc, component);
        await component.StartAsync(ctx, ct);
    }

    public async Task<string> UnloadAsync(string id, CancellationToken ct = default)
    {
        if (!_loaded.TryGetValue(id, out var p)) return $"{id}: 未加载";
        // 级联卸载：先卸载正在消费它服务的插件
        foreach (var consumer in _composition.ConsumersOf(id).ToList())
            await UnloadAsync(consumer, ct);
        await _composition.RollbackAsync(id);
        _loaded.Remove(id);
        p.Alc.Unload();
        Changed?.Invoke("unload");
        return $"{id}: 已卸载";
    }

    public async Task<string> DeleteAsync(string id, CancellationToken ct = default)
    {
        var msg = await UnloadAsync(id, ct);
        var desc = _catalog.FirstOrDefault(c => c.Id == id);
        if (desc != null)
        {
            // 卸载后 GC 才真正释放文件锁；尽力删除
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            try { File.Delete(desc.FilePath); msg += "，dll 已删除"; }
            catch (IOException) { msg += "，dll 文件仍被占用，删除失败"; }
            Rescan();
        }
        Changed?.Invoke("delete");
        return msg;
    }

    public string SnapshotJson() => JsonSerializer.Serialize(new
    {
        catalog = _catalog.Select(c => new { id = c.Id, description = c.Description, loaded = _loaded.ContainsKey(c.Id) }),
    }, _json);

    // ---- 装载完成门（stdio 等传输插件必须等全部插件装载完成后再开始收发请求，避免竞态） ----
    private static readonly TaskCompletionSource LoadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>宿主在全量装载结束后调用；重复调用幂等。</summary>
    public static void SignalLoadComplete() => LoadGate.TrySetResult();

    /// <summary>传输插件等待装载完成。15 秒超时兜底放行（非宿主场景）。</summary>
    public static async Task WaitForLoadCompletionAsync(CancellationToken ct = default)
    {
        var done = await Task.WhenAny(LoadGate.Task, Task.Delay(15000, ct)).ConfigureAwait(false);
        if (done != LoadGate.Task) ct.ThrowIfCancellationRequested();
    }

    private sealed record LoadedPlugin(string Id, AssemblyLoadContext Alc, IComponent Component);
}
