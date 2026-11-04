// SubSsh.Plugin.Search — 搜索引擎应用插件（只 Require 公共组件，不对外提供底层服务之外的接口）。
// 对应 OneSSH internal/searchx：Grep（rg --json，exit 127 降级 helper→SFTP）、
// Find（fd/fdfind，--full-path 规则，降级同上）。helper 二进制路径经 ISettings.SearchHelper，
// 为空则直接走 SFTP 降级（对应 Go HelperEnabled=false）。
// 依赖：Require ISettings（SearchHelper）、IConnectionPool；Provide<ISearchEngine>。
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;

namespace SubSsh.Plugin.Search;

[Plugin("search", "搜索引擎：远端 rg/fd 优先 + helper 协议 + SFTP 降级（可被同接口插件替换）")]
public sealed class SearchPlugin : IComponent
{
    public string Name => "search";

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        SearchManager? manager = null;
        // Require ISettings（SearchHelper 决定 helper 降级是否可用）。
        ctx.Require<ISettings>(
            s =>
            {
                ctx.Require<IConnectionPool>(
                    pool =>
                    {
                        if (manager != null)
                        {
                            ctx.Provide<ISearchEngine>(manager);
                            return;
                        }
                        manager = new SearchManager(pool, s);
                        ctx.Provide<ISearchEngine>(manager);
                    },
                    () => ctx.Unprovide<ISearchEngine>());
            },
            () => ctx.Unprovide<ISearchEngine>());
        return Task.CompletedTask;
    }
}
