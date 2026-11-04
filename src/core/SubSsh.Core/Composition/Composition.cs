namespace SubSsh.Core.Composition;

/// <summary>组合根：服务注册表 + 可撤销效果栈 + 依赖图。</summary>
public sealed class Composition
{
    private readonly object _lock = new();
    private readonly Dictionary<Type, (object Impl, string Provider)> _services = new();
    // 依赖边：consumer 插件名 -> (服务类型, onAdd, onRemove)
    private readonly List<(string Consumer, Type Service, Delegate OnAdd, Action OnRemove)> _edges = new();
    // 每个插件的 revert 效果栈（LIFO）
    private readonly Dictionary<string, List<Func<Task>>> _reverts = new();

    public event Action<Type>? ServiceAdded;
    public event Action<Type>? ServiceRemoved;

    public void Provide<TService>(string provider, TService impl) where TService : class
    {
        List<(string Consumer, Delegate OnAdd, Action OnRemove)> toAdd = new();
        lock (_lock)
        {
            _services[typeof(TService)] = (impl, provider);
            foreach (var e in _edges)
                if (e.Service == typeof(TService))
                    toAdd.Add((e.Consumer, e.OnAdd, e.OnRemove));
        }
        foreach (var (consumer, onAdd, _) in toAdd)
        {
            onAdd.DynamicInvoke(impl);
            _reverts[consumer].Add(() => Task.Run(() => { }));
        }
        ServiceAdded?.Invoke(typeof(TService));
    }

    public void RemoveService<TService>() where TService : class
    {
        List<(string Consumer, Action OnRemove)> toRemove = new();
        lock (_lock)
        {
            if (!_services.Remove(typeof(TService))) return;
            foreach (var e in _edges)
                if (e.Service == typeof(TService))
                    toRemove.Add((e.Consumer, e.OnRemove));
        }
        foreach (var (_, onRemove) in toRemove) onRemove();
        ServiceRemoved?.Invoke(typeof(TService));
    }

    public TService? Get<TService>() where TService : class
    {
        lock (_lock)
            return _services.TryGetValue(typeof(TService), out var s) ? (TService)s.Impl : null;
    }

    public string? ProviderOf<TService>() where TService : class
    {
        lock (_lock)
            return _services.TryGetValue(typeof(TService), out var s) ? s.Provider : null;
    }

    public void AddEdge(string consumer, Type service, Delegate onAdd, Action onRemove)
    {
        lock (_lock)
        {
            _edges.Add((consumer, service, onAdd, onRemove));
            Console.Error.WriteLine($"[edge] add {consumer} wants {service.FullName} (asm={service.Assembly.GetName().Name}); keys=[{string.Join(",", _services.Keys.Select(k => k.FullName + "@" + k.Assembly.GetName().Name))}]");
            if (_services.TryGetValue(service, out var s))
            {
                Console.Error.WriteLine($"[edge] fire {consumer} for {service.FullName} (provider={s.Provider})");
                onAdd.DynamicInvoke(s.Impl);
                if (!_reverts.TryGetValue(consumer, out var stack))
                    _reverts[consumer] = stack = new();
                stack.Add(() => Task.Run(onRemove));
            }
        }
    }

    public void PushRevert(string plugin, Func<Task> revert)
    {
        lock (_lock)
        {
            if (!_reverts.TryGetValue(plugin, out var stack))
                _reverts[plugin] = stack = new();
            stack.Add(revert);
        }
    }

    /// <summary>按 LIFO 回滚某插件的全部效果并移除其依赖边。</summary>
    public async Task RollbackAsync(string plugin)
    {
        List<Func<Task>> stack;
        List<(string, Type, Delegate, Action)> edges;
        lock (_lock)
        {
            stack = _reverts.TryGetValue(plugin, out var s) ? new(s) : new();
            _reverts.Remove(plugin);
            edges = _edges.Where(e => e.Consumer == plugin).ToList();
            foreach (var e in edges) _edges.Remove(e);
        }
        foreach (var (_, _, _, onRemove) in edges) onRemove();
        for (var i = stack.Count - 1; i >= 0; i--)
            await stack[i]();
    }

    /// <summary>正在消费指定插件所提供服务的插件名（用于级联卸载）。</summary>
    public List<string> ConsumersOf(string providerPlugin)
    {
        lock (_lock)
        {
            var services = _services.Where(kv => kv.Value.Provider == providerPlugin).Select(kv => kv.Key).ToHashSet();
            return _edges.Where(e => services.Contains(e.Service)).Select(e => e.Consumer).Distinct().ToList();
        }
    }

    public List<string> LoadedPlugins
    {
        get { lock (_lock) return _reverts.Keys.ToList(); }
    }
}
