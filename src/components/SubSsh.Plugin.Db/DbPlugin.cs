// SubSsh.Plugin.Db — 公共 DB 组件插件（可插拔、可任意替换：换同接口插件即可替换存储实现）。
// GPL-3.0，移植自 OneSSH internal/store（migration 原样嵌入）。
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;

namespace SubSsh.Plugin.Db;

[Plugin("db", "公共 DB 组件：SQLite 存储 + 原版 migration（可被同接口插件替换）")]
public sealed class DbPlugin : IComponent
{
    public string Name => "db";

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        SqliteDb? db = null;
        ctx.Require<ISettings>(
            s =>
            {
                // 空间可组合：设置上线即建库发布；重复激活时复用已有实例
                if (db != null)
                {
                    ctx.Provide<IOneSshStore>(db);
                    ctx.Provide<IOneSshStoreFull>(db);
                    return;
                }
                db = SqliteDb.Open(s.DataDir);
                // 时间可组合：关闭连接登记为可撤销效果，卸载时自动执行
                ctx.Track(
                    apply: () => { },
                    revert: () => db.DisposeAsync().AsTask());
                ctx.Provide<IOneSshStore>(db);
                ctx.Provide<IOneSshStoreFull>(db);
            },
            () =>
            {
                // 提供者下线：静默撤回发布，等待设置插件回来
                ctx.Unprovide<IOneSshStoreFull>();
                ctx.Unprovide<IOneSshStore>();
            });
        return Task.CompletedTask;
    }
}

// SQLite 实现位于 SqliteDb.cs（完整 IOneSshStoreFull）。
