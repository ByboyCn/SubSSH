namespace SubSsh.Core.Events;

/// <summary>进程内事件总线（对应原版 internal/events/events.go）。</summary>
public sealed class EventBus
{
    private readonly object _lock = new();
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();

    public void Subscribe<TEvent>(Action<TEvent> handler)
    {
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
                _handlers[typeof(TEvent)] = list = new();
            list.Add(handler);
        }
    }

    public void Unsubscribe<TEvent>(Action<TEvent> handler)
    {
        lock (_lock)
            if (_handlers.TryGetValue(typeof(TEvent), out var list))
                list.Remove(handler);
    }

    public void Publish<TEvent>(TEvent evt)
    {
        Delegate[] snapshot;
        lock (_lock)
            snapshot = _handlers.TryGetValue(typeof(TEvent), out var list) ? list.ToArray() : [];
        foreach (var d in snapshot)
            if (d is Action<TEvent> h) h(evt);
    }
}
