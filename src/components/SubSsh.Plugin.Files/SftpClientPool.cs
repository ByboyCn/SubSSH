// 对应 Go internal/files/client.go 的 ClientPool：按主机缓存 SFTP 客户端，
// 连接池返回同一 ssh 连接时复用 SFTP 通道，连接重建后旧通道关闭重建。
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.Files;

internal sealed class SftpClientPool(IConnectionPool pool) : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (IRemoteConnection Conn, IRemoteSftp Sftp)> _clients = [];

    /// <summary>取得（或复用）指定主机的 SFTP 客户端（对应 Go ClientPool.Get）。</summary>
    public async Task<IRemoteSftp> GetAsync(string host, CancellationToken ct = default)
    {
        var conn = await pool.GetAsync(host, ct);
        lock (_lock)
        {
            if (_clients.TryGetValue(host, out var cached) && ReferenceEquals(cached.Conn, conn))
                return cached.Sftp;
            if (_clients.Remove(host, out var old))
                _ = old.Sftp.DisposeAsync(); // 对应 Go old.sftp.Close()
        }
        IRemoteSftp sftp;
        try
        {
            sftp = await conn.OpenSftpAsync(ct);
        }
        catch (Exception ex)
        {
            pool.Invalidate(host);
            throw new SubSshException($"创建 SFTP 客户端: {ex.Message}");
        }
        lock (_lock) _clients[host] = (conn, sftp);
        return sftp;
    }

    /// <summary>关闭全部缓存客户端（对应 Go ClientPool.Close）。</summary>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        IRemoteSftp[] all;
        lock (_lock)
        {
            all = [.. _clients.Values.Select(c => c.Sftp)];
            _clients.Clear();
        }
        foreach (var s in all) await s.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        GC.SuppressFinalize(this);
    }
}

/// <summary>解析 ~ 与 ~/path 为远端绝对路径（对应 Go files.resolvePath / sftp.Client.Getwd）。</summary>
internal static class RemotePathResolver
{
    public static async Task<string> ResolveAsync(IRemoteSftp c, string path, CancellationToken ct = default)
    {
        if (path != "~" && !path.StartsWith("~/", StringComparison.Ordinal))
            return path;
        var home = await c.GetwdAsync(ct);
        if (path == "~") return home;
        return home + "/" + path[2..];
    }
}
