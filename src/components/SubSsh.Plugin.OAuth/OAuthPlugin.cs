// SubSsh.Plugin.OAuth — 公共 OAuth 组件插件（可插拔、可任意替换）。
// 全量移植 OneSSH internal/oauthserver：PKCE S256、动态客户端注册、
// .well-known 元数据、授权码/刷新令牌（含重放撤销整 grant）。
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;

namespace SubSsh.Plugin.OAuth;

[Plugin("oauth", "公共 OAuth 组件：PKCE + 动态客户端注册 + .well-known 元数据（可被同接口插件替换）")]
public sealed class OAuthPlugin : IComponent
{
    public string Name => "oauth";

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        OAuthService? svc = null;
        SqliteOAuthStore? store = null;
        // Require ISettings（PublicURL、MasterKey）与 IOneSshStore（持久化）。
        ctx.Require<ISettings>(
            s =>
            {
                ctx.Require<IOneSshStore>(
                    st =>
                    {
                        if (svc != null) { ctx.Provide<IOAuthService>(svc); ctx.Provide<IOAuthServer>(svc); return; }
                        store = new SqliteOAuthStore(st.ConnectionString);
                        svc = new OAuthService(s.PublicUrl, s.MasterKey, store);
                        ctx.Provide<IOAuthService>(svc);
                        ctx.Provide<IOAuthServer>(svc);
                    },
                    () =>
                    {
                        ctx.Unprovide<IOAuthService>();
                        ctx.Unprovide<IOAuthServer>();
                    });
            },
            () =>
            {
                ctx.Unprovide<IOAuthServer>();
                ctx.Unprovide<IOAuthService>();
            });
        return Task.CompletedTask;
    }
}
