// 对应 Go internal/files/manager.go 的 Manager：
// Read/RawRead/Write/Edit/List/Transfer/OpenRead；sha256 乐观锁、临时文件+rename 原子写、
// 二进制拒绝、目录 500 项上限、传输 10 分钟超时 + 落盘 sha256sum 校验。
using System.Security.Cryptography;
using System.Text;
using SubSsh.Core.Abstractions;
using SubSsh.Core.Models;

namespace SubSsh.Plugin.Files;

public sealed class FileManager(IConnectionPool pool) : IFileManager
{
    /// <summary>文本读取上限 4 MiB（对应 Go maxFile = 4 &lt;&lt; 20）。</summary>
    private const long MaxFile = 4L << 20;

    /// <summary>传输落盘校验的命令超时（对应 Go execx.Options{Timeout: 30s, MaxLines: 2}）。</summary>
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(30);

    private readonly SftpClientPool _clients = new(pool);

    /// <summary>关闭 SFTP 客户端缓存（插件卸载时由 ctx.Track 登记调用）。</summary>
    public Task CloseAsync(CancellationToken ct = default) => _clients.CloseAsync(ct);

    // -----------------------------------------------------------------------
    // OpenRead —— Go Manager.OpenRead：Stat 校验大小上限后打开只读流。
    // -----------------------------------------------------------------------
    public async Task<(Stream Stream, long Size)> OpenReadAsync(string host, string file, long max, CancellationToken ct = default)
    {
        var c = await _clients.GetAsync(host, ct);
        file = await RemotePathResolver.ResolveAsync(c, file, ct);
        var info = await c.StatAsync(file, ct);
        if (info.Size > max)
            throw new SubSshException($"文件大小 {info.Size} 超过限制 {max}");
        var f = await c.OpenReadAsync(file, ct);
        return (f, info.Size);
    }

    // -----------------------------------------------------------------------
    // RawRead —— Go Manager.RawRead：OpenRead + LimitReader(max+1) 全量读。
    // -----------------------------------------------------------------------
    public async Task<byte[]> RawReadAsync(string host, string file, long max, CancellationToken ct = default)
    {
        var (f, _) = await OpenReadAsync(host, file, max, ct);
        await using (f.ConfigureAwait(false))
        {
            var buffer = new MemoryStream();
            var buf = new byte[64 * 1024];
            long remaining = max + 1; // 对应 Go io.LimitReader(f, max+1)
            while (remaining > 0)
            {
                var want = (int)Math.Min((long)buf.Length, remaining);
                var n = await f.ReadAsync(buf.AsMemory(0, want), ct);
                if (n <= 0) break;
                buffer.Write(buf, 0, n);
                remaining -= n;
            }
            return buffer.ToArray();
        }
    }

    // -----------------------------------------------------------------------
    // Read —— Go Manager.Read：二进制嗅探拒绝、sha256、行号前缀分页。
    // -----------------------------------------------------------------------
    public async Task<FileReadResult> ReadAsync(string host, string file, int offset, int limit, CancellationToken ct = default)
    {
        var data = await RawReadAsync(host, file, MaxFile, ct);
        var sniffLen = Math.Min(data.Length, 8192);
        for (var i = 0; i < sniffLen; i++)
        {
            if (data[i] == 0)
                throw new SubSshException("binary file；请使用 image_view 或 WebUI 下载");
        }
        var sum = SHA256.HashData(data);
        // 字节保真的字符串视图：对应 Go 的 byte→string 直转（非法 UTF-8 不被替换）。
        var text = Encoding.Latin1.GetString(data);
        var lines = text.Split('\n');
        var total = lines.Length;
        if (data.Length > 0 && data[^1] == (byte)'\n')
        {
            total--;
            lines = lines[..^1];
        }
        if (offset <= 0) offset = 1;
        if (limit <= 0 || limit > 5000) limit = 500;
        var start = Math.Min(offset - 1, lines.Length);
        var end = Math.Min(start + limit, lines.Length);
        var b = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            b.Append(i + 1).Append(':').Append(lines[i]);
            if (i + 1 < end) b.Append('\n');
        }
        return new FileReadResult
        {
            Content = b.ToString(),
            Sha256 = Hex(sum),
            Bytes = data.Length,
            TotalLines = total,
        };
    }

    // -----------------------------------------------------------------------
    // Write —— Go Manager.Write：MkdirAll + 临时文件 + chmod + rename（失败降级非原子）。
    // -----------------------------------------------------------------------
    public async Task<FileWriteResult> WriteAsync(string host, string file, byte[] data, int mode, CancellationToken ct = default)
    {
        var c = await _clients.GetAsync(host, ct);
        file = await RemotePathResolver.ResolveAsync(c, file, ct);
        await c.MkdirAllAsync(PosixPath.Dir(file), ct);
        var tmp = file + ".onessh-tmp-" + RandomSuffix();
        try
        {
            await using (var f = await c.OpenWriteAsync(tmp, ct))
            {
                await f.WriteAsync(data, ct);
                await f.FlushAsync(ct);
            }
        }
        catch
        {
            await SafeRemove(c, tmp);
            throw;
        }
        try
        {
            await c.ChmodAsync(tmp, mode, ct);
        }
        catch
        {
            await SafeRemove(c, tmp);
            throw;
        }
        var (nonAtomic, renameError) = await RenameWithFallbackAsync(c, tmp, file, ct);
        if (renameError != null)
        {
            await SafeRemove(c, tmp);
            throw renameError;
        }
        var sum = SHA256.HashData(data);
        var result = new FileWriteResult { Sha256 = Hex(sum), Bytes = data.Length, NonAtomic = nonAtomic };
        if (nonAtomic) result = result with { Warning = "目标服务器不支持覆盖 rename，已使用非原子回退" };
        return result;
    }

    // -----------------------------------------------------------------------
    // Edit —— Go Manager.Edit：sha256 乐观锁、old_text 唯一匹配、unified diff、0644 写回。
    // -----------------------------------------------------------------------
    public async Task<FileEditResult> EditAsync(string host, string file, IReadOnlyList<FileEdit> edits, string expectedSha256, CancellationToken ct = default)
    {
        var data = await RawReadAsync(host, file, MaxFile, ct);
        var current = Hex(SHA256.HashData(data));
        if (!string.IsNullOrEmpty(expectedSha256) && expectedSha256 != current)
            throw new SubSshException($"conflict: expected_sha256={expectedSha256} actual_sha256={current}；请重读文件");
        var originalText = Encoding.Latin1.GetString(data);
        var updated = originalText;
        for (var i = 0; i < edits.Count; i++)
        {
            var oldText = edits[i].OldText;
            var count = CountNonOverlapping(updated, oldText);
            if (count != 1)
                throw new SubSshException($"edit {i + 1}: old_text 必须唯一匹配，实际 {count} 次");
            var at = updated.IndexOf(oldText, StringComparison.Ordinal);
            updated = updated[..at] + edits[i].NewText + updated[(at + oldText.Length)..];
        }
        var diff = UnifiedDiff.Create(SplitLines(originalText), SplitLines(updated), file, file, context: 3);
        var wr = await WriteAsync(host, file, Encoding.Latin1.GetBytes(updated), mode: 420 /* 0644 */, ct);
        return new FileEditResult
        {
            Sha256 = wr.Sha256,
            Bytes = wr.Bytes,
            NonAtomic = wr.NonAtomic,
            Warning = wr.Warning,
            Diff = diff,
        };
    }

    // -----------------------------------------------------------------------
    // List —— Go Manager.List：ReadDir、500 项上限、软链接目标、目录优先按名排序。
    // -----------------------------------------------------------------------
    public async Task<IReadOnlyList<FileEntry>> ListAsync(string host, string dir, CancellationToken ct = default)
    {
        var c = await _clients.GetAsync(host, ct);
        dir = await RemotePathResolver.ResolveAsync(c, dir, ct);
        var items = await c.ReadDirAsync(dir, ct);
        if (items.Count > 500)
            throw new SubSshException("目录超过 500 项，请缩小范围");
        var outList = new List<FileEntry>(items.Count);
        foreach (var info in items)
        {
            string? symlinkTarget = null;
            if (info.Mode.StartsWith('l')) // 对应 Go info.Mode()&os.ModeSymlink != 0
                symlinkTarget = await c.ReadLinkAsync(PosixPath.Join(dir, info.Name), ct);
            outList.Add(new FileEntry
            {
                Name = info.Name,
                Size = info.Size,
                Mode = info.Mode,
                Mtime = info.Mtime,
                Directory = info.Directory,
                SymlinkTarget = symlinkTarget,
            });
        }
        outList.Sort((x, y) =>
        {
            if (x.Directory != y.Directory) return x.Directory ? -1 : 1;
            return string.CompareOrdinal(x.Name, y.Name);
        });
        return outList;
    }

    // -----------------------------------------------------------------------
    // Transfer —— Go Manager.Transfer：10 分钟硬超时、流式复制 + 边写边哈希、
    // rename 落盘、远端 sha256sum 校验（缺命令时 → 警告跳过校验）。
    // -----------------------------------------------------------------------
    public async Task<FileTransferResult> TransferAsync(string srcHost, string srcPath, string dstHost, string dstPath, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10)); // 对应 Go context.WithTimeout(ctx, 10*time.Minute)
        var tct = timeoutCts.Token;
        var src = await _clients.GetAsync(srcHost, tct);
        var dst = await _clients.GetAsync(dstHost, tct);
        srcPath = await RemotePathResolver.ResolveAsync(src, srcPath, tct);
        dstPath = await RemotePathResolver.ResolveAsync(dst, dstPath, tct);
        var input = await src.OpenReadAsync(srcPath, tct);
        long n;
        string sourceHashHex;
        var tmp = dstPath + ".onessh-tmp-" + RandomSuffix();
        await using (input.ConfigureAwait(false))
        {
            await dst.MkdirAllAsync(PosixPath.Dir(dstPath), tct);
            var output = await dst.OpenWriteAsync(tmp, tct);
            var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); // 对应 io.MultiWriter(out, hash)
            n = 0;
            try
            {
                var buf = new byte[64 * 1024];
                int read;
                while ((read = await input.ReadAsync(buf.AsMemory(), tct)) > 0)
                {
                    hash.AppendData(buf, 0, read);
                    await output.WriteAsync(buf.AsMemory(0, read), tct);
                    n += read;
                }
                await output.FlushAsync(tct);
            }
            catch
            {
                await output.DisposeAsync();
                await SafeRemove(dst, tmp);
                throw;
            }
            sourceHashHex = Hex(hash.GetHashAndReset());
            await output.DisposeAsync();
        }
        // 对应 Go：rename 失败 → Remove 目标后重试；再失败 → Remove tmp 并报错（不标 non_atomic）。
        var (_, renameError) = await RenameWithFallbackAsync(dst, tmp, dstPath, tct);
        if (renameError != null)
        {
            await SafeRemove(dst, tmp);
            throw renameError;
        }
        var result = new FileTransferResult { Bytes = n, SourceSha256 = sourceHashHex };
        // 落盘校验：对应 Go m.Pool.Get + m.Exec.Run（sha256sum | cut -d' ' -f1，exit 127 → 降级）。
        try
        {
            var sshClient = await pool.GetAsync(dstHost, tct);
            var output = await RunSha256sumAsync(sshClient, dstPath, tct);
            if (output != null)
            {
                result = result with
                {
                    DestinationSha256 = output,
                    Verified = output == sourceHashHex,
                };
            }
            else
            {
                result = result with { Warning = "目标缺少 sha256sum，已跳过落盘校验" };
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = result with { Warning = "目标缺少 sha256sum，已跳过落盘校验" };
        }
        catch (Exception)
        {
            result = result with { Warning = "目标缺少 sha256sum，已跳过落盘校验" };
        }
        return result;
    }

    // -----------------------------------------------------------------------
    // 内部工具
    // -----------------------------------------------------------------------

    /// <summary>
    /// 在目标主机执行 sha256sum 校验命令（对应 Go m.Exec.Run 的那条 if command -v sha256sum …）。
    /// 返回第一行去空白输出；命令缺失（exit 127）或执行失败返回 null。
    /// </summary>
    private static async Task<string?> RunSha256sumAsync(IRemoteConnection sshClient, string dstPath, CancellationToken ct)
    {
        await using var session = await sshClient.OpenSessionAsync(ct);
        var command =
            "if command -v sha256sum >/dev/null 2>&1; then sha256sum -- " + Shq(dstPath) +
            " | cut -d' ' -f1; else exit 127; fi";
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(VerifyTimeout);
        try
        {
            await session.StartAsync(command, timeoutCts.Token);
            var buffer = new MemoryStream();
            var buf = new byte[8 * 1024];
            long budget = 64 * 1024; // 对应 MaxLines: 2 的输出预算（只取首行，粗放限额即可）
            while (budget > 0)
            {
                var want = (int)Math.Min((long)buf.Length, budget);
                var read = await session.Stdout.ReadAsync(buf.AsMemory(0, want), timeoutCts.Token);
                if (read <= 0) break;
                buffer.Write(buf, 0, read);
                budget -= read;
            }
            var exit = await session.WaitAsync(timeoutCts.Token);
            if (exit != 0) return null; // 含 exit 127（sha256sum 缺失）与非零哈希命令失败
            var firstLine = Encoding.UTF8.GetString(buffer.ToArray());
            var nl = firstLine.IndexOf('\n');
            if (nl >= 0) firstLine = firstLine[..nl];
            return firstLine.Trim();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await session.KillAsync(ct);
            return null;
        }
    }

    /// <summary>rename 降级：对应 Go 先 Remove 目标再 rename 重试；返回是否非原子与最终错误。</summary>
    private static async Task<(bool NonAtomic, Exception? Error)> RenameWithFallbackAsync(IRemoteSftp c, string tmp, string file, CancellationToken ct)
    {
        try
        {
            await c.RenameAsync(tmp, file, ct);
            return (false, null);
        }
        catch (Exception)
        {
            await SafeRemove(c, file);
            try
            {
                await c.RenameAsync(tmp, file, ct);
                return (true, null);
            }
            catch (Exception ex2)
            {
                return (true, ex2);
            }
        }
    }

    private static async Task SafeRemove(IRemoteSftp c, string path)
    {
        try { await c.RemoveAsync(path); } catch { /* 对应 Go 忽略删除错误 */ }
    }

    private static int CountNonOverlapping(string text, string value)
    {
        if (value.Length == 0) return 0;
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(value, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += value.Length; // 对应 Go strings.Count 的非重叠计数
        }
        return count;
    }

    private static string[] SplitLines(string s)
    {
        // 对应 go-difflib SplitLines：按 \n 切分，每行保留换行符（末行可能没有）。
        if (s.Length == 0) return [];
        var parts = s.Split('\n');
        var lines = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            lines[i] = i < parts.Length - 1 ? parts[i] + "\n" : parts[i];
        return lines;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string RandomSuffix()
    {
        var b = new byte[6];
        RandomNumberGenerator.Fill(b);
        return Convert.ToHexString(b).ToLowerInvariant();
    }

    /// <summary>单引号安全 shell 转义（语义对齐 Go execx.SHQ）。</summary>
    private static string Shq(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
