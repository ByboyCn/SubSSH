// 命令会话 —— 包装 Renci.SshNet SshCommand，对应 Go 的 *ssh.Session 能力面。
// SshCommand 在 StartAsync 时按命令文本创建（Renci 2024.2 的 CommandText 只能在构造时给定）。
using Renci.SshNet;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.SshPool;

internal sealed class SshCommandSession(Func<string, SshCommand> factory) : IRemoteSession
{
    private SshCommand? _cmd;
    private IAsyncResult? _ar;
    private readonly TaskCompletionSource<int?> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _killed;

    public Stream Stdout => EnsureCmd().OutputStream;
    public Stream Stderr => EnsureCmd().ExtendedOutputStream;

    /// <summary>对应 Go session.Start。</summary>
    public Task StartAsync(string command, CancellationToken ct = default)
    {
        if (_cmd != null)
            throw new InvalidOperationException("会话已启动");
        _cmd = factory(command);
        _ar = _cmd.BeginExecute();
        _ = Task.Run(() =>
        {
            try
            {
                _cmd!.EndExecute(_ar!);
                _tcs.TrySetResult(_cmd.ExitStatus);
            }
            catch
            {
                // 命令被终止或连接断开：退出码未知（对应 Go ExitError 不可解析时返回 null）。
                _tcs.TrySetResult(null);
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>对应 Go session.Wait + ExitError；未知退出码返回 null。</summary>
    public async Task<int?> WaitAsync(CancellationToken ct = default)
    {
        using var reg = ct.Register(static s => ((SshCommandSession)s!).KillCore(), this);
        try
        {
            return await _tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>终止远端进程（对应 Go terminateSession：先 SSH SIGKILL 再关 channel）。</summary>
    public Task KillAsync(CancellationToken ct = default)
    {
        KillCore();
        return Task.CompletedTask;
    }

    private void KillCore()
    {
        _killed = true;
        try { _cmd?.CancelAsync(); } catch { }
    }

    private SshCommand EnsureCmd() =>
        _cmd ?? throw new InvalidOperationException("会话尚未启动（先调用 StartAsync）");

    public ValueTask DisposeAsync()
    {
        _cmd?.Dispose();
        return ValueTask.CompletedTask;
    }
}
