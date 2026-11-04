// SubSsh.Plugin.SshPool — 公共 SSH 池组件插件（可插拔、可任意替换：换同接口插件即可替换连接实现）。
// GPL-3.0，移植自 SubSSH internal/sshpool/pool.go（连接复用、TOFU 指纹固定、跳板链最多 5 级且不能成环）。
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;

namespace SubSsh.Plugin.SshPool;

[Plugin("ssh-pool", "公共 SSH 池组件：Renci.SshNet 连接复用、TOFU 主机指纹、跳板链（可被同接口插件替换）")]
public sealed class SshPoolPlugin : IComponent
{
    public string Name => "ssh-pool";

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        ISubSshStoreFull? store = null;
        ICryptoBox? box = null;
        SshPool? pool = null;

        // 空间可组合：两个依赖都上线才建池发布；任一下线撤回发布并静默等待。
        void Activate()
        {
            if (pool != null || store == null || box == null) return;
            pool = new SshPool(store, box);
            // 时间可组合：关闭全部连接登记为可撤销效果，卸载时自动执行。
            ctx.Track(
                apply: () => { },
                revert: () => pool.CloseAsync().GetAwaiter().GetResult());
            ctx.Provide<IConnectionPool>(pool);
        }

        void Deactivate()
        {
            if (pool == null) return;
            ctx.Unprovide<IConnectionPool>();
            pool.CloseAsync().GetAwaiter().GetResult();
            pool = null;
        }

        ctx.Require<ISubSshStoreFull>(
            s => { store = s; Activate(); },
            () => { store = null; Deactivate(); });
        ctx.Require<ICryptoBox>(
            b => { box = b; Activate(); },
            () => { box = null; Deactivate(); });
        return Task.CompletedTask;
    }
}
