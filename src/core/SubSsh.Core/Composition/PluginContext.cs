namespace SubSsh.Core.Composition;

/// <summary>IContext 的默认实现，绑定插件名与组合根。</summary>
public sealed class PluginContext(string pluginName, Composition composition, Core.Events.EventBus bus) : IContext
{
    public void Provide<TService>(TService impl) where TService : class
        => composition.Provide(pluginName, impl);

    public void Unprovide<TService>() where TService : class
        => composition.RemoveService<TService>();

    public void Require<TService>(Action<TService> onAdd, Action onRemove) where TService : class
        => composition.AddEdge(pluginName, typeof(TService), onAdd, onRemove);

    public void Track(Action apply, Action revert)
    {
        apply();
        composition.PushRevert(pluginName, () => Task.Run(revert));
    }

    public void Track(Func<Task> apply, Func<Task> revert)
    {
        apply().GetAwaiter().GetResult();
        composition.PushRevert(pluginName, revert);
    }

    public void Subscribe<TEvent>(Action<TEvent> handler)
        => Track(() => bus.Subscribe(handler), () => bus.Unsubscribe(handler));
}
