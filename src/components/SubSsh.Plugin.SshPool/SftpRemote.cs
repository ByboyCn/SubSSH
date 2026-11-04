// SFTP 能力面 —— 包装 Renci.SshNet SftpClient，对应 Go files 使用的 sftp.Client 方法面。
using System.Reflection;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.SshPool;

internal sealed class SftpRemote(SftpClient c) : IRemoteSftp
{
    public Task<RemoteFileStat> StatAsync(string path, CancellationToken ct = default)
    {
        var a = c.GetAttributes(path);
        return Task.FromResult(ToStat(path, a));
    }

    public Task<IReadOnlyList<RemoteFileStat>> ReadDirAsync(string path, CancellationToken ct = default)
    {
        var items = c.ListDirectory(path)
            .Where(f => f.Name is not (".") and not (".."))
            .Select(f => ToStat(f.Name, f.Attributes))
            .ToList();
        IReadOnlyList<RemoteFileStat> result = items;
        return Task.FromResult(result);
    }

    public Task MkdirAllAsync(string path, CancellationToken ct = default)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cur = path.StartsWith('/') ? "/" : "";
        foreach (var part in parts)
        {
            cur = cur.Length == 0 || cur == "/" ? cur + part : cur + "/" + part;
            if (!c.Exists(cur))
            {
                c.CreateDirectory(cur);
            }
            else if (!c.GetAttributes(cur).IsDirectory)
            {
                throw new SubSsh.Core.Abstractions.SubSshException($"mkdir {cur}: 不是目录");
            }
        }
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<Stream>(c.OpenRead(path));

    public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<Stream>(c.OpenWrite(path));

    public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
    {
        c.RenameFile(oldPath, newPath);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string path, CancellationToken ct = default)
    {
        c.DeleteFile(path);
        return Task.CompletedTask;
    }

    public Task ChmodAsync(string path, int mode, CancellationToken ct = default)
    {
        c.ChangePermissions(path, (short)mode);
        return Task.CompletedTask;
    }

    /// <summary>读软链接目标；不是软链接或远端不支持时返回 null（对应 Go c.ReadLink）。</summary>
    public Task<string?> ReadLinkAsync(string path, CancellationToken ct = default)
    {
        try
        {
            // Renci 2024.2.0 无公开 ReadLink；经内部 SftpSession.RequestReadLink（SSH_FXP_READLINK）。
            var session = typeof(SftpClient)
                .GetProperty("SftpSession", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(c)!;
            var method = session.GetType().GetMethod(
                "RequestReadLink", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            return Task.FromResult<string?>((string)method.Invoke(session, [path, true])!);
        }
        catch (Exception)
        {
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>远端当前工作目录（对应 Go sftp.Client.Getwd，用于解析 ~）。</summary>
    public Task<string> GetwdAsync(CancellationToken ct = default) =>
        Task.FromResult(c.WorkingDirectory);

    public ValueTask DisposeAsync()
    {
        c.Dispose();
        return ValueTask.CompletedTask;
    }

    private static RemoteFileStat ToStat(string name, SftpFileAttributes a)
    {
        var mode =
            (a.IsSymbolicLink ? "l" : a.IsDirectory ? "d" : "-")
            + Triplet(a.OwnerCanRead, a.OwnerCanWrite, a.OwnerCanExecute)
            + Triplet(a.GroupCanRead, a.GroupCanWrite, a.GroupCanExecute)
            + Triplet(a.OthersCanRead, a.OthersCanWrite, a.OthersCanExecute);
        var mtime = new DateTimeOffset(a.LastWriteTimeUtc).ToUnixTimeSeconds();
        return new RemoteFileStat(name, a.Size, mode, mtime, a.IsDirectory);
    }

    private static string Triplet(bool r, bool w, bool x) =>
        (r ? "r" : "-") + (w ? "w" : "-") + (x ? "x" : "-");
}
