namespace SubSsh.Core.Composition;

/// <summary>
/// 插件上下文。时空可组合性入口：
/// - Provide/Require 实现空间可组合（提供者上线依赖者自动激活、下线自动停用）
/// - Track 实现时间可组合（一切修改为可撤销效果，卸载按 LIFO 自动回滚）
/// </summary>
public interface IContext
{
    /// <summary>发布一个服务实现。重复发布同类型会替换旧实现并触发依赖者重绑。</summary>
    void Provide<TService>(TService impl) where TService : class;

    /// <summary>撤回本插件对 TService 的发布（依赖者收到 onRemove，静默等待新提供者）。</summary>
    void Unprovide<TService>() where TService : class;

    /// <summary>声明依赖：服务可用时调用 onAdd（已在可用时立即调用），不可用时调用 onRemove。</summary>
    void Require<TService>(Action<TService> onAdd, Action onRemove) where TService : class;

    /// <summary>登记可撤销效果。卸载时按注册逆序执行 revert。</summary>
    void Track(Action apply, Action revert);

    /// <summary>登记异步可撤销效果。</summary>
    void Track(Func<Task> apply, Func<Task> revert);

    /// <summary>订阅事件总线（卸载自动退订）。</summary>
    void Subscribe<TEvent>(Action<TEvent> handler);
}
