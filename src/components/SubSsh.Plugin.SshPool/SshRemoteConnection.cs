// 一条已建立的远程连接 —— 包装 Renci.SshNet SshClient，提供会话/SFTP/隧道能力面。
// 对应 Go 的 *ssh.Client；跳板隧道经本连接上的 SOCKS5 转发端口（ForwardedPortDynamic）按需启用。
using System.Net;
using System.Net.Sockets;
using Renci.SshNet;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.SshPool;

internal sealed class SshRemoteConnection : IRemoteConnection
{
    private readonly SshClient _client;
    private readonly SshPool _pool;
    private readonly object _portGate = new();
    private ForwardedPortDynamic? _port;

    internal SshRemoteConnection(SshClient client, string name, SshPool pool, IReadOnlyList<string> via)
    {
        _client = client;
        Name = name;
        _pool = pool;
        Via = via;
    }

    internal string Name { get; }

    /// <summary>到达本连接所经的跳板链（不含本主机），用于对外 DialAsync 的环检测。</summary>
    internal IReadOnlyList<string> Via { get; }

    /// <summary>对应 Go ssh.Client.NewSession。</summary>
    public Task<IRemoteSession> OpenSessionAsync(CancellationToken ct = default)
    {
        // Renci 2024.2 的 CreateCommand 需要 CommandText；命令在 StartAsync 时创建。
        return Task.FromResult<IRemoteSession>(new SshCommandSession(commandText => _client.CreateCommand(commandText)));
    }

    /// <summary>打开 SFTP 通道（对应 Go files 背后的 sftp.Client）。</summary>
    public async Task<IRemoteSftp> OpenSftpAsync(CancellationToken ct = default)
    {
        var sftp = new SftpClient(_client.ConnectionInfo);
        try
        {
            await sftp.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            sftp.Dispose();
            throw new SubSshException($"打开 SFTP 通道 {Name}: {ex.Message}");
        }
        return new SftpRemote(sftp);
    }

    /// <summary>
    /// 经本连接向目标主机建立 SSH 连接（对应 Go jc.DialContext + newSSHClient 的组合面）。
    /// host 须为主机库中的主机名；port 为 0 时使用主机配置端口。跳板链环检测与上限沿用池规则。
    /// </summary>
    public Task<IRemoteConnection> DialAsync(string host, int port, CancellationToken ct = default) =>
        Task.FromResult<IRemoteConnection>(_pool.DialThroughAsync(this, host, port, ct).GetAwaiter().GetResult());

    /// <summary>
    /// 确保本连接上的 SOCKS5 转发端口已启动，返回其本地绑定地址（供池经 ProxyTypes.Socks5 拨号）。
    /// 对应 Go jc.DialContext 的隧道面。
    /// </summary>
    internal Task<(string Host, int Port)> EnsureProxyAsync()
    {
        lock (_portGate)
        {
            if (_port == null)
            {
                // 绑定到 OS 临时端口，避免与其他转发冲突。
                var ephemeral = TakeFreePort();
                _port = new ForwardedPortDynamic("127.0.0.1", ephemeral);
                _client.AddForwardedPort(_port);
                _port.Start();
            }
            if (!_port.IsStarted)
                throw new InvalidOperationException("跳板转发端口未启动");
            var boundHost = _port.BoundHost is "0.0.0.0" or "::" ? "127.0.0.1" : _port.BoundHost;
            return Task.FromResult((boundHost, (int)_port.BoundPort));
        }
    }

    internal void SendKeepAlive()
    {
        // 对应 Go c.SendRequest("keepalive@onessh", true, nil)。
#pragma warning disable CS0618
        _client.SendKeepAlive();
#pragma warning restore CS0618
    }

    /// <summary>关闭本连接（SshClient + 转发端口），并从池里摘除登记。</summary>
    public void Dispose()
    {
        lock (_portGate)
        {
            if (_port != null)
            {
                try { if (_port.IsStarted) _port.Stop(); } catch { }
                try { _client.RemoveForwardedPort(_port); } catch { }
                _port = null;
            }
        }
        try { _client.Dispose(); } catch { }
        _pool.MarkOffline(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static uint TakeFreePort()
    {
        var tmp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            tmp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return (uint)((IPEndPoint)tmp.LocalEndPoint!).Port;
        }
        finally
        {
            tmp.Dispose();
        }
    }
}
