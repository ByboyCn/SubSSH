// SubSsh.Plugin.WebApi — 公共 Web 组件插件（可插拔、可任意替换）。
// Requires：ISettings、ISubSshStoreFull（构造 WebApiStoreAdapter 适配为 IWebApiStore 并 Provide）、
// IOAuthService（必需）+ IWebApiHostManager/IWebApiConnectionPool/IWebApiCommandRunner/
// IWebApiJobManager/IWebApiFileManager（可选，上线自动绑入、下线自动解绑）。
// Provide：IWebApiServer + IWebApiStore（原版 internal/webapi 的完整移植）。
namespace SubSsh.Plugin.WebApi;

using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;
using SubSsh.Core.Events;

[Plugin("webapi", "公共 Web 组件：REST /api/v1 + SSE + WS 终端 + Vue SPA 托管（可被同接口插件替换）")]
public sealed class WebApiPlugin : IComponent
{
    public string Name => "webapi";

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        KestrelWebServer? server = null;
        var events = new EventBus();
        // SSE 总线对外 Provide（IWebApiEventBus.Publish）：McpCore/Jobs 等组件经 Require 桥接
        // job_status/tool_call/command_finished 等事件进 /api/events。
        var sse = new WebApiBus(events);

        void StopServer()
        {
            if (server == null) { return; }
            ctx.Unprovide<IWebApiServer>();
            ctx.Unprovide<IWebApiEventBus>();
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            server = null;
        }

        void Bind(KestrelWebServer s)
        {
            ctx.Require<IWebApiHostManager>(h => s.SetHostManager(h), () => s.SetHostManager(null));
            ctx.Require<IWebApiConnectionPool>(p => s.SetConnectionPool(p), () => s.SetConnectionPool(null));
            ctx.Require<IWebApiCommandRunner>(r => s.SetCommandRunner(r), () => s.SetCommandRunner(null));
            ctx.Require<IWebApiJobManager>(j => s.SetJobManager(j), () => s.SetJobManager(null));
            ctx.Require<IWebApiFileManager>(f => s.SetFileManager(f), () => s.SetFileManager(null));
        }

        ctx.Require<ISettings>(
            settings =>
            {
                ctx.Require<ISubSshStoreFull>(
                    store =>
                    {
                        // Db 插件提供 ISubSshStoreFull，这里适配成 WebApi 需要的窄接口并对外 Provide，
                        // 使其他消费 IWebApiStore 的组件无需感知存储全量接口。
                        var adapter = new WebApiStoreAdapter(store);
                        ctx.Provide<IWebApiStore>(adapter);
                        ctx.Require<IOAuthService>(
                            oauth =>
                            {
                                if (server != null) { ctx.Provide<IWebApiServer>(server); ctx.Provide<IWebApiEventBus>(sse); return; }
                                                            server = KestrelWebServer.BuildAndStart(settings, adapter, oauth, events);
                            ctx.Require<ICryptoBox>(c => server.SetCrypto(c), () => server.SetCrypto(null));
                                // Crypto 插件上线后私钥入库自动加密
                                Bind(server);
                                ctx.Provide<IWebApiServer>(server);
                                ctx.Provide<IWebApiEventBus>(sse);
                            },
                            () => { /* oauth 下线：等待回来 */ });
                    },
                    () =>
                    {
                        // store 下线：撤掉适配层，并连带停掉依赖它的 server。
                        ctx.Unprovide<IWebApiStore>();
                        StopServer();
                    });
            },
            () =>
            {
                StopServer();
                ctx.Unprovide<IWebApiStore>();
            });
        return Task.CompletedTask;
    }
}
