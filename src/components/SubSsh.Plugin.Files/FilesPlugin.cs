// SubSsh.Plugin.Files — 公共文件组件插件（可插拔、可任意替换：换同接口插件即可替换文件管理实现）。
// GPL-3.0，移植自 SubSSH internal/files（manager.go / client.go）。
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;

namespace SubSsh.Plugin.Files;

[Plugin("files", "公共文件组件：file read/write/edit/list/transfer，sha256 乐观锁、原子写（可被同接口插件替换）")]
public sealed class FilesPlugin : IComponent
{
    public string Name => "files";

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        FileManager? manager = null;
        ctx.Require<IConnectionPool>(
            pool =>
            {
                // 空间可组合：连接池上线即发布文件管理器；重复激活时复用已有实例
                if (manager != null)
                {
                    ctx.Provide<IFileManager>(manager);
                    return;
                }
                manager = new FileManager(pool);
                // 时间可组合：关闭 SFTP 客户端缓存登记为可撤销效果，卸载时自动执行
                ctx.Track(
                    apply: () => { },
                    revert: () => manager.CloseAsync());
                ctx.Provide<IFileManager>(manager);
            },
            () =>
            {
                // 提供者下线：静默撤回发布，等待连接池插件回来
                ctx.Unprovide<IFileManager>();
            });
        return Task.CompletedTask;
    }
}

// 实现位于 FileManager.cs / SftpClientPool.cs / PosixPath.cs / UnifiedDiff.cs。
