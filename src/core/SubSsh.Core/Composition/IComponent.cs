namespace SubSsh.Core.Composition;

/// <summary>插件标识特性：宿主扫描时自动读取。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class PluginAttribute(string id, string description = "") : Attribute
{
    public string Id { get; } = id;
    public string Description { get; } = description;
}

/// <summary>插件入口：实现此接口的类型由宿主发现并实例化。</summary>
public interface IComponent
{
    string Name { get; }
    Task StartAsync(IContext ctx, CancellationToken ct = default);
}
