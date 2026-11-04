// SubSsh.Plugin.HostManager — 插件装配（组合根入口）。
// Provide: IHostManager（对应 Go hostmanager.New(store, box, pool)）。
// Require: ISubSshStoreFull + ICryptoBox（必需，缺任一则不发布）；IConnectionPool（可选，仅 Test 需要）。
// 空间可组合：任一必需提供者下线即静默撤回发布，回来自动恢复；本插件不对外提供 WebApi 适配
//（IWebApiHostManager 由 Plugin.WebApi 自行适配，见 WebApiAbstractions.cs）。
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;

namespace SubSsh.Plugin.HostManager;

[Plugin("host-manager", "主机管理组件：Create/Update/Delete/Test/ResetFingerprint/Archive/Unarchive，标签与跳板链校验、错误分类（可被同接口插件替换）")]
public sealed class HostManagerPlugin : IComponent
{
    public string Name => "host-manager";

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        var manager = new HostManager();
        ISubSshStoreFull? store = null;
        ICryptoBox? box = null;

        // 必需依赖齐备才发布；任一下线即撤回（依赖者静默等待）
        void Publish()
        {
            if (store != null && box != null)
            {
                manager.SetStore(store);
                manager.SetCrypto(box);
                ctx.Provide<IHostManager>(manager);
            }
            else
            {
                ctx.Unprovide<IHostManager>();
            }
        }

        ctx.Require<ISubSshStoreFull>(
            s => { store = s; Publish(); },
            () => { store = null; ctx.Unprovide<IHostManager>(); });

        ctx.Require<ICryptoBox>(
            b => { box = b; Publish(); },
            () => { box = null; ctx.Unprovide<IHostManager>(); });

        // 可选依赖：池可用即接入（Test 生效），下线时仅 Test 报明确错误
        ctx.Require<IConnectionPool>(
            p => manager.SetPool(p),
            () => manager.SetPool(null));

        return Task.CompletedTask;
    }
}
