// SubSsh.Plugin.ExecRunner — 公共命令执行组件插件（可插拔、可任意替换）。
// 全量移植 SubSSH internal/execx/runner.go：超时/截断（head+tail）/退出码+cwd trailer/
// artifact 与 command output 落盘、ReadCommandOutput 字节游标分页、ReadArtifact 行分页+grep、
// SHQ/Script 包装脚本；session cwd/env 持久化经 ISubSshStoreFull。
// 依赖：Require ISettings（DataDir）、IConnectionPool（上线门控）、ISubSshStoreFull（session 存取）。
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;

namespace SubSsh.Plugin.ExecRunner;

[Plugin("exec-runner", "公共命令执行组件：超时/截断/session cwd+env 持久化/artifact（可被同接口插件替换）")]
public sealed class ExecRunnerPlugin : IComponent
{
    public string Name => "exec-runner";

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        ExecRunnerService? svc = null;
        // Require ISettings（DataDir 决定 artifacts/ 与 command-runs/ 落盘位置）。
        ctx.Require<ISettings>(
            s =>
            {
                ctx.Require<IConnectionPool>(
                    _ =>
                    {
                        ctx.Require<ISubSshStoreFull>(
                            st =>
                            {
                                if (svc != null)
                                {
                                    ctx.Provide<ICommandRunner>(svc);
                                    ctx.Provide<IExecSessionPersistence>(svc);
                                    return;
                                }
                                svc = new ExecRunnerService(s.DataDir, st);
                                ctx.Provide<ICommandRunner>(svc);
                                ctx.Provide<IExecSessionPersistence>(svc);
                            },
                            () =>
                            {
                                ctx.Unprovide<IExecSessionPersistence>();
                                ctx.Unprovide<ICommandRunner>();
                            });
                    },
                    () =>
                    {
                        ctx.Unprovide<IExecSessionPersistence>();
                        ctx.Unprovide<ICommandRunner>();
                    });
            },
            () =>
            {
                ctx.Unprovide<IExecSessionPersistence>();
                ctx.Unprovide<ICommandRunner>();
            });
        return Task.CompletedTask;
    }
}

/// <summary>
/// exec session（cwd/env）持久化能力（对应 Go mcpserver exec 工具的 session 存取面，
/// 底层走 ISubSshStoreFull.GetSessionAsync/SaveSessionAsync）。与 ICommandRunner 同实例提供。
/// </summary>
public interface IExecSessionPersistence
{
    /// <summary>读 session；不存在时返回默认值（Cwd="~"、空 env），与 Go 版一致。</summary>
    Task<SessionRecord> GetAsync(long tokenId, long hostId, string label, CancellationToken ct = default);

    /// <summary>保存 session（更新后的 cwd/env）。</summary>
    Task SaveAsync(SessionRecord session, CancellationToken ct = default);

    /// <summary>合并 env 键值并保存（对应 Go exec 工具 setenv：覆盖已有键、追加新键）。</summary>
    Task<SessionRecord> SetEnvAsync(long tokenId, long hostId, string label, IReadOnlyDictionary<string, string> vars, CancellationToken ct = default);

    /// <summary>删除 env 键并保存（对应 Go exec 工具 unsetenv）。</summary>
    Task<SessionRecord> UnsetEnvAsync(long tokenId, long hostId, string label, IReadOnlyList<string> keys, CancellationToken ct = default);
}
