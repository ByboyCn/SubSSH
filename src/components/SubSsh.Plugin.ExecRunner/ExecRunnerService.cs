// SubSsh.Plugin.ExecRunner — ExecRunnerService：execx/runner.go 全量 C# 移植。
// 语义 1:1：256 KiB 捕获上限（head=(limit+1)/2 + tail）、唯一 trailer 标记
// "\n\x01ONESSH:<uuid>:"、超时先 SSH SIGKILL 再关会话、artifact 拼接捕获文件、
// 字节游标分页在 UTF-8 字符边界截断。
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.ExecRunner;

public sealed partial class ExecRunnerService : ICommandRunner, IExecSessionPersistence
{
    private const int CaptureLimit = 256 << 10;

    private readonly string _dataDir;
    private readonly IOneSshStoreFull _store;

    public ExecRunnerService(string dataDir, IOneSshStoreFull store)
    {
        _dataDir = dataDir;
        _store = store;
    }

    // ------------------------------------------------------------------
    // SHQ / Script（Go execx.SHQ / Script / commandScript）
    // ------------------------------------------------------------------

    /// <summary>单引号安全转义（对应 Go execx.SHQ）。</summary>
    public static string SHQ(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>cd + export env + 命令包装脚本（对应 Go execx.Script）。</summary>
    public static string Script(string command, string cwd, IReadOnlyDictionary<string, string>? env)
        => CommandScript(command, cwd, env, "");

    private static string CommandScript(string command, string cwd, IReadOnlyDictionary<string, string>? env, string trailerId)
    {
        var b = new StringBuilder();
        b.Append("cd ").Append(SHQ(cwd)).Append(" 2>/dev/null || cd \"$HOME\"\n");
        if (env != null)
        {
            var keys = new List<string>(env.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var k in keys)
            {
                b.Append("export ").Append(k).Append('=').Append(SHQ(env[k])).Append('\n');
            }
        }
        b.Append("{ ").Append(command).Append("\n}\n__ec=$?\nprintf '\\n\\001ONESSH:");
        if (trailerId.Length != 0)
        {
            b.Append(trailerId).Append(':');
        }
        b.Append("%d:%s\\001' \"$__ec\" \"$PWD\"\n");
        return b.ToString();
    }

    // ------------------------------------------------------------------
    // Run（Go Runner.Run）
    // ------------------------------------------------------------------

    public async Task<ExecResult> RunAsync(
        IRemoteConnection client, string command, string cwd,
        IReadOnlyDictionary<string, string>? env, ExecOptions opt, CancellationToken ct = default)
    {
        var timeout = opt.Timeout is { } t && t > TimeSpan.Zero ? t : TimeSpan.FromSeconds(60);
        var maxLines = opt.MaxLines <= 0 ? 200 : opt.MaxLines;

        var capture = opt.CaptureId.Length != 0 ? await OpenCommandCaptureAsync(opt.CaptureId) : null;

        var trailerId = Guid.NewGuid().ToString("D");
        var trailerMarker = Encoding.UTF8.GetBytes("\n\x01ONESSH:" + trailerId + ":");

        var stdout = new LimitedWriter(CaptureLimit, "stdout", opt.OnOutput, stripTrailer: true, trailerMarker);
        var stderr = new LimitedWriter(CaptureLimit, "stderr", opt.OnOutput, stripTrailer: false, []);

        IRemoteSession session;
        try
        {
            session = await client.OpenSessionAsync(ct);
        }
        catch
        {
            capture?.Close();
            capture?.Remove();
            throw;
        }

        try
        {
            if (capture != null)
            {
                stdout.Full = capture.Stdout;
                stderr.Full = capture.Stderr;
            }

            await session.StartAsync(CommandScript(command, cwd, env, trailerId), ct);
        }
        catch
        {
            await session.DisposeAsync();
            if (capture != null)
            {
                capture.Close();
                capture.Remove();
            }
            throw;
        }

        await using var sessionDispose = session;

        // 等待：session.Wait 与超时/取消三者任一（对应 Go select done/timer/ctx.Done）。
        var waitTask = session.WaitAsync(CancellationToken.None);
        var timeoutTask = Task.Delay(timeout, CancellationToken.None);
        var cancelTask = Task.Delay(Timeout.Infinite, ct);
        var timedOut = false;
        bool waitFaulted;
        Exception? waitException = null;
        int? waitExitCode = null;
        try
        {
            var done = await Task.WhenAny(waitTask, timeoutTask, cancelTask);
            if (done != waitTask)
            {
                timedOut = true;
                // 必须先发 SSH SIGKILL；只关闭 channel 不会终止远端进程组（对应 Go terminateSession）。
                try { await session.KillAsync(); } catch { /* 尽力终止 */ }
                try { await waitTask; } catch (Exception ex) { waitException = ex; }
            }
            if (waitTask.IsCompleted)
            {
                try
                {
                    waitExitCode = await waitTask;
                    waitFaulted = false;
                }
                catch (Exception ex)
                {
                    waitException = ex;
                    waitFaulted = true;
                }
            }
            else
            {
                waitFaulted = waitException != null;
            }
        }
        finally
        {
            cancelTask.Dispose();
            timeoutTask.Dispose();
        }

        // 等 EOF：缓冲区里剩余的输出全部进入捕获（对应 Go session.Wait 读到 EOF 后才返回）。
        try { await Task.WhenAll(PumpAsync(session.Stdout, stdout), PumpAsync(session.Stderr, stderr)); }
        catch { /* pump 内部已吞错 */ }

        var fullStdout = stdout.Captured();
        var (exitCode, newCwd, clean, parsed) = ParseTrailerWithMarker(fullStdout, trailerMarker);
        var trailerBytes = 0;
        if (parsed)
        {
            trailerBytes = fullStdout.Length - clean.Length;
        }
        fullStdout = clean;
        var exitCodeKnown = parsed;
        if (!parsed)
        {
            // 对应 Go exitCodeFromError：waitErr == nil → 0；非 ExitError 的错误 → -1。
            exitCode = waitFaulted ? -1 : (waitExitCode ?? 0);
            newCwd = cwd;
            exitCodeKnown = waitExitCode.HasValue;
        }
        var stderrBytes = stderr.Captured();
        stdout.FinishCallback();
        stderr.FinishCallback();

        var captureErr = (string?)null;
        if (capture != null)
        {
            var errs = new List<string>();
            try { capture.Close(); }
            catch (Exception ex) { errs.Add(ex.Message); }
            if (parsed)
            {
                try { StripCaptureTrailer(capture.StdoutPath, trailerMarker); }
                catch (Exception ex) { errs.Add(ex.Message); }
            }
            if (errs.Count != 0) captureErr = string.Join("; ", errs);
        }
        if (stdout.FullError is { } fe) captureErr = captureErr == null ? fe.Message : captureErr + "; " + fe.Message;
        if (stderr.FullError is { } fe2) captureErr = captureErr == null ? fe2.Message : captureErr + "; " + fe2.Message;

        var combined = new byte[fullStdout.Length + stderrBytes.Length];
        fullStdout.CopyTo(combined, 0);
        stderrBytes.CopyTo(combined, fullStdout.Length);
        var (selected, capturedLines, lineCut) = SelectLines(combined, maxLines, opt.Tail);
        var totalLines = capturedLines;
        if (stdout.Truncated || stderr.Truncated)
        {
            totalLines = stdout.Newlines + stderr.Newlines;
            if (parsed) totalLines--;
        }
        var stdoutTotal = Math.Max(0, stdout.Written - trailerBytes);
        var outputRecorded = opt.CaptureId.Length != 0 && capture != null && captureErr == null;

        var res = new ExecResult
        {
            Stdout = Utf8Text(fullStdout),
            Stderr = Utf8Text(stderrBytes),
            Output = Utf8Text(selected),
            ExitCode = exitCode,
            ExitCodeKnown = exitCodeKnown,
            Cwd = newCwd,
            Timeout = timedOut,
            Truncated = stdout.Truncated || stderr.Truncated || lineCut,
            TotalLines = totalLines,
            TotalBytes = stdoutTotal + stderr.Written,
            StdoutBytes = stdoutTotal,
            StderrBytes = stderr.Written,
            OutputRecorded = outputRecorded,
            OutputCaptureError = captureErr,
        };

        if (res.Truncated)
        {
            var artifactsDir = Path.Combine(_dataDir, "artifacts");
            Directory.CreateDirectory(artifactsDir);
            var id = Guid.NewGuid().ToString("D");
            var artifactPath = Path.Combine(artifactsDir, id + ".log");
            if (res.OutputRecorded && capture != null)
            {
                ConcatenateFiles(artifactPath, capture.StdoutPath, capture.StderrPath);
            }
            else
            {
                File.WriteAllBytes(artifactPath, combined);
            }
            res = res with { ArtifactId = id };
        }

        // 对应 Go：waitErr != nil && !parsed && !timedOut 且不是 ExitError 时把错误返回给调用方。
        if (waitFaulted && !parsed && !timedOut)
        {
            throw new SubSshException(waitException?.Message ?? "命令执行失败");
        }
        return res;
    }

    private static async Task PumpAsync(Stream src, LimitedWriter w)
    {
        var buf = new byte[64 * 1024];
        try
        {
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                w.Write(buf.AsSpan(0, n));
            }
        }
        catch
        {
            // 终止会话后的读取错误不参与结果（对应 Go 的 channel 关闭行为）。
        }
    }

    private static string Utf8Text(byte[] data)
        => Encoding.UTF8.GetString(data); // 非法 UTF-8 由解码器替换为 U+FFFD（TODO: 与 Go ToValidUTF8 每个非法 run 一个替换字符的计数语义对齐）

    // ------------------------------------------------------------------
    // 捕获文件（Go commandCapture / openCommandCapture / stripCaptureTrailer / concatenateFiles）
    // ------------------------------------------------------------------

    private sealed class CommandCapture : IDisposable
    {
        public required FileStream Stdout;
        public required FileStream Stderr;
        public required string StdoutPath;
        public required string StderrPath;

        public void Close()
        {
            var errs = new List<Exception>();
            try { Stdout.Dispose(); } catch (Exception ex) { errs.Add(ex); }
            try { Stderr.Dispose(); } catch (Exception ex) { errs.Add(ex); }
            if (errs.Count != 0) throw new AggregateException(errs);
        }

        public void Remove()
        {
            try { File.Delete(StdoutPath); } catch { /* 尽力 */ }
            try { File.Delete(StderrPath); } catch { /* 尽力 */ }
        }

        public void Dispose() => Close();
    }

    private async Task<CommandCapture> OpenCommandCaptureAsync(string id)
    {
        var stdoutPath = await CommandOutputPathAsync(id, "stdout");
        var stderrPath = await CommandOutputPathAsync(id, "stderr");
        Directory.CreateDirectory(Path.GetDirectoryName(stdoutPath)!);
        FileStream stdout;
        try
        {
            stdout = new FileStream(stdoutPath, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        catch (Exception ex)
        {
            throw new SubSshException($"打开命令输出捕获失败: {ex.Message}");
        }
        FileStream stderr;
        try
        {
            stderr = new FileStream(stderrPath, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        catch (Exception ex)
        {
            stdout.Dispose();
            File.Delete(stdoutPath);
            throw new SubSshException($"打开命令输出捕获失败: {ex.Message}");
        }
        return new CommandCapture { Stdout = stdout, Stderr = stderr, StdoutPath = stdoutPath, StderrPath = stderrPath };
    }

    private static void StripCaptureTrailer(string path, byte[] marker)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var size = fs.Length;
        const int tailLimit = 64 << 10;
        var start = Math.Max(0, size - tailLimit);
        var tail = new byte[size - start];
        fs.Position = start;
        var read = 0;
        while (read < tail.Length)
        {
            var n = fs.Read(tail, read, tail.Length - read);
            if (n <= 0) break;
            read += n;
        }
        tail = tail[..read];
        var index = LastIndex(tail, marker);
        if (index < 0)
        {
            throw new SubSshException("完整输出缺少命令 trailer");
        }
        fs.SetLength(start + index);
    }

    private static void ConcatenateFiles(string destination, params string[] sources)
    {
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        foreach (var source in sources)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            input.CopyTo(output);
        }
    }

    // ------------------------------------------------------------------
    // Trailer 解析与行选择（Go parseTrailerWithMarker / exitCodeFromError / selectLines）
    // ------------------------------------------------------------------

    private static (int ExitCode, string Cwd, byte[] Clean, bool Parsed) ParseTrailerWithMarker(byte[] data, byte[] marker)
    {
        var idx = LastIndex(data, marker);
        if (idx < 0) return (0, "", data, false);
        var start = idx + marker.Length;
        var end = Array.IndexOf(data, (byte)1, start);
        if (end < 0) return (0, "", data, false);
        var payload = Encoding.UTF8.GetString(data, start, end - start);
        var sep = payload.IndexOf(':');
        if (sep < 0) return (0, "", data, false);
        if (!int.TryParse(payload.AsSpan(0, sep), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ec))
        {
            return (0, "", data, false);
        }
        return (ec, payload[(sep + 1)..], data[..idx], true);
    }

    private static (byte[] Selected, int TotalLines, bool LineCut) SelectLines(byte[] data, int maxLines, bool tail)
    {
        if (data.Length == 0) return ([], 0, false);
        var lines = SplitLines(data);
        var total = lines.Length;
        if (data[^1] == (byte)'\n') total--;
        if (total <= maxLines) return (data, total, false);
        if (tail)
        {
            return (JoinLines(lines[^maxLines..]), total, true);
        }
        return (JoinLines(lines[..maxLines]), total, true);
    }

    private static byte[][] SplitLines(byte[] data)
    {
        var lines = new List<byte[]>();
        var start = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == (byte)'\n')
            {
                lines.Add(data[start..i]);
                start = i + 1;
            }
        }
        lines.Add(data[start..]);
        return [.. lines];
    }

    private static byte[] JoinLines(byte[][] lines)
    {
        if (lines.Length == 0) return [];
        var total = lines.Sum(l => l.Length) + lines.Length - 1;
        var result = new byte[total];
        var pos = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) result[pos++] = (byte)'\n';
            lines[i].CopyTo(result, pos);
            pos += lines[i].Length;
        }
        return result;
    }

    private static int LastIndex(byte[] data, byte[] marker)
    {
        if (marker.Length == 0 || marker.Length > data.Length) return -1;
        for (var i = data.Length - marker.Length; i >= 0; i--)
        {
            var ok = true;
            for (var j = 0; j < marker.Length; j++)
            {
                if (data[i + j] != marker[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    // ------------------------------------------------------------------
    // 清理（Go CleanupArtifacts / CleanupCommandOutputs）
    // ------------------------------------------------------------------

    public Task<int> CleanupArtifactsAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var dir = Path.Combine(_dataDir, "artifacts");
        if (!Directory.Exists(dir)) return Task.FromResult(0);
        var removed = 0;
        var errors = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry) || Path.GetExtension(name) != ".log") continue;
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "D", out _)) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(entry) < cutoff.UtcDateTime)
                {
                    File.Delete(entry);
                    removed++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"删除 artifact {name}: {ex.Message}");
            }
        }
        if (errors.Count != 0) throw new SubSshException(string.Join("; ", errors));
        return Task.FromResult(removed);
    }

    /// <summary>
    /// 只删除数据库明确允许清理的托管 UUID.stdout/stderr.log 文件（删除白名单，
    /// 避免数据库快照之后新启动的命令文件被误当成垃圾）。
    /// </summary>
    public Task<int> CleanupCommandOutputsAsync(IReadOnlyCollection<string> deleteIds, CancellationToken ct = default)
    {
        var dir = Path.Combine(_dataDir, "command-runs");
        if (!Directory.Exists(dir)) return Task.FromResult(0);
        var allowed = new HashSet<string>(deleteIds, StringComparer.Ordinal);
        var removed = 0;
        var errors = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry)) continue;
            var parts = name.Split('.');
            if (parts.Length != 3 || (parts[1] != "stdout" && parts[1] != "stderr") || parts[2] != "log") continue;
            if (!Guid.TryParseExact(parts[0], "D", out _)) continue;
            if (!allowed.Contains(parts[0])) continue;
            try
            {
                File.Delete(entry);
                removed++;
            }
            catch (Exception ex)
            {
                errors.Add($"删除命令输出 {name}: {ex.Message}");
            }
        }
        if (errors.Count != 0) throw new SubSshException(string.Join("; ", errors));
        return Task.FromResult(removed);
    }

    // ------------------------------------------------------------------
    // 路径与分页读取（Go ArtifactPath / CommandOutputPath / ReadCommandOutput / ReadArtifact）
    // ------------------------------------------------------------------

    public Task<string> ArtifactPathAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(id, "D", out _))
        {
            throw new SubSshException("artifact_id 无效");
        }
        return Task.FromResult(Path.Combine(_dataDir, "artifacts", id + ".log"));
    }

    public Task<string> CommandOutputPathAsync(string id, string stream, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(id, "D", out _))
        {
            throw new SubSshException("run_id 无效");
        }
        if (stream != "stdout" && stream != "stderr")
        {
            throw new SubSshException("stream 仅支持 stdout 或 stderr");
        }
        return Task.FromResult(Path.Combine(_dataDir, "command-runs", id + "." + stream + ".log"));
    }

    public Task<OutputChunk> ReadCommandOutputAsync(string id, string stream, long offset, int limit, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var path = CommandOutputPathAsync(id, stream).GetAwaiter().GetResult();
            if (!File.Exists(path))
            {
                throw new SubSshException("命令输出已过期或不存在");
            }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var size = fs.Length;
            if (offset < 0) offset = 0;
            if (offset > size) offset = size;
            if (limit <= 0) limit = 256 << 10;
            if (limit > 1 << 20) limit = 1 << 20;
            var readLimit = limit + 3; // utf8.UTFMax - 1
            var length = (int)Math.Min(readLimit, size - offset);
            var data = new byte[length];
            fs.Position = offset;
            var read = 0;
            while (read < length)
            {
                var n = fs.Read(data, read, length - read);
                if (n <= 0) break;
                read += n;
            }
            data = data[..read];
            data = Utf8Page(data, limit, offset + read < size);
            var rawBytes = data.Length;
            var next = offset + rawBytes;
            // 游标始终按原始字节推进；非法 UTF-8 只在文本响应里替换，不能改变下一页偏移。
            return new OutputChunk
            {
                Content = Utf8Text(data),
                Offset = offset,
                NextOffset = next,
                TotalBytes = size,
                Complete = next >= size,
            };
        }, ct);

    public Task<(string Content, int TotalLines)> ReadArtifactAsync(string path, int offset, int limit, string pattern, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!File.Exists(path))
            {
                throw new SubSshException("artifact 已过期或不存在");
            }
            var data = File.ReadAllBytes(path);
            var lines = SplitLines(Encoding.UTF8.GetBytes(Utf8Text(data)));
            var total = lines.Length;
            if (data.Length > 0 && data[^1] == (byte)'\n')
            {
                total--;
                lines = lines[..^1];
            }
            Regex? re = null;
            if (pattern.Length != 0)
            {
                try
                {
                    re = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
                }
                catch (ArgumentException ex)
                {
                    throw new SubSshException($"grep 正则无效: {ex.Message}");
                }
            }
            if (offset <= 0) offset = 1;
            if (limit <= 0 || limit > 5000) limit = 200;
            var matched = new List<string>();
            for (var i = 0; i < lines.Length; i++)
            {
                var line = Utf8Text(lines[i]);
                if (re == null || re.IsMatch(line))
                {
                    matched.Add($"{i + 1}:{line}");
                }
            }
            var start = Math.Min(offset - 1, matched.Count);
            var end = Math.Min(start + limit, matched.Count);
            return (string.Join("\n", matched[start..end]), total);
        }, ct);

    // ------------------------------------------------------------------
    // UTF-8 分页（Go CompleteUTF8Prefix / UTF8Page）
    // ------------------------------------------------------------------

    /// <summary>避免文本分页恰好切在 UTF-8 字符中间；被保留的尾部字节由下一页从相同偏移重读。</summary>
    public static byte[] CompleteUtf8Prefix(byte[] data, bool hasMore)
    {
        if (!hasMore || data.Length == 0) return data;
        var start = data.Length - 1;
        while (start > 0 && !RuneStart(data[start])) start--;
        return Utf8FullRune(data, start) ? data : data[..start];
    }

    /// <summary>在 limit 内取一个完整字符边界；limit 小到放不下首个字符时允许超出到字符末尾。</summary>
    public static byte[] Utf8Page(byte[] data, int limit, bool hasMore)
    {
        if (limit <= 0 || data.Length <= limit)
        {
            return CompleteUtf8Prefix(data, hasMore);
        }
        var page = CompleteUtf8Prefix(data[..limit], hasMore: true);
        if (page.Length > 0) return page;
        var size = Math.Clamp(Utf8RuneSize(data), 1, data.Length);
        return data[..size];
    }

    private static bool RuneStart(byte b) => (b & 0xC0) != 0x80;

    private static int Utf8RuneSize(byte[] data)
    {
        if (data.Length == 0) return 0;
        var b0 = data[0];
        if (b0 < 0x80) return 1;
        if ((b0 & 0xE0) == 0xC0) return 2;
        if ((b0 & 0xF0) == 0xE0) return 3;
        if ((b0 & 0xF8) == 0xF0) return 4;
        return 0;
    }

    private static bool Utf8FullRune(byte[] data, int offset)
    {
        var remaining = data.Length - offset;
        if (remaining <= 0) return false;
        var size = Utf8RuneSize(data.AsSpan(offset).ToArray());
        if (size <= 0 || size > remaining) return false;
        if (size == 1) return true;
        for (var i = 1; i < size; i++)
        {
            if (!RuneStart(data[offset + i])) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------
    // session cwd/env 持久化（Go mcpserver exec 工具的 session 存取面，走 IOneSshStoreFull）
    // ------------------------------------------------------------------

    public async Task<SessionRecord> GetAsync(long tokenId, long hostId, string label, CancellationToken ct = default)
        => await _store.GetSessionAsync(tokenId, hostId, label, ct);

    public Task SaveAsync(SessionRecord session, CancellationToken ct = default)
        => _store.SaveSessionAsync(session, ct);

    public async Task<SessionRecord> SetEnvAsync(long tokenId, long hostId, string label, IReadOnlyDictionary<string, string> vars, CancellationToken ct = default)
    {
        var session = await _store.GetSessionAsync(tokenId, hostId, label, ct);
        var env = new Dictionary<string, string>(session.Env, StringComparer.Ordinal);
        foreach (var (k, v) in vars) env[k] = v;
        session.Env = env;
        await _store.SaveSessionAsync(session, ct);
        return session;
    }

    public async Task<SessionRecord> UnsetEnvAsync(long tokenId, long hostId, string label, IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        var session = await _store.GetSessionAsync(tokenId, hostId, label, ct);
        var env = new Dictionary<string, string>(session.Env, StringComparer.Ordinal);
        foreach (var k in keys) env.Remove(k);
        session.Env = env;
        await _store.SaveSessionAsync(session, ct);
        return session;
    }
}

// ---------------------------------------------------------------------------
// LimitedWriter —— Go limitedWriter 的 C# 移植：
// 256 KiB 捕获上限（head=(limit+1)/2 + tail 滚动）、换行计数、完整输出旁路写盘、
// 输出回调（stdout 剥离自家 trailer，marker 横跨 SSH 数据块安全）。
// ---------------------------------------------------------------------------
internal sealed class LimitedWriter(int limit, string stream, OutputCallback? callback, bool stripTrailer, byte[] trailerMarker)
{
    private readonly MemoryStream _buf = new();
    private byte[] _tail = [];
    private byte[] _pending = [];
    private readonly byte[] _marker = stripTrailer && trailerMarker.Length == 0 ? LegacyMarker : trailerMarker;
    private bool _stopped;

    private static readonly byte[] LegacyMarker = "\n\x01ONESSH:"u8.ToArray();

    public Stream? Full { get; set; }
    public Exception? FullError { get; private set; }

    public int Written { get; private set; }
    public int Newlines { get; private set; }
    public bool Truncated { get; private set; }

    public void Write(ReadOnlySpan<byte> p)
    {
        if (Full != null && FullError == null)
        {
            try
            {
                Full.Write(p);
            }
            catch (Exception ex)
            {
                // 记录失败不能反过来打断已经在远端执行的命令；最终结果会明确携带捕获错误。
                FullError = ex;
            }
        }
        EmitCallback(p);
        Written += p.Length;
        foreach (var b in p)
        {
            if (b == (byte)'\n') Newlines++;
        }
        var headLimit = (limit + 1) / 2;
        var remain = Math.Max(0, headLimit - (int)_buf.Length);
        var headLen = Math.Min(remain, p.Length);
        _buf.Write(p[..headLen]);
        if (headLen < p.Length)
        {
            var tailLimit = limit - headLimit;
            var newTail = new byte[_tail.Length + (p.Length - headLen)];
            _tail.CopyTo(newTail, 0);
            p[headLen..].CopyTo(newTail.AsSpan(_tail.Length));
            if (newTail.Length > tailLimit)
            {
                _tail = newTail[^tailLimit..];
            }
            else
            {
                _tail = newTail;
            }
        }
        Truncated = Written > limit;
    }

    // stdout 最后附有 Runner 自己的退出码/cwd trailer。回调只暂存 marker 长度的尾巴，
    // 既能处理 marker 横跨两个 SSH 数据块，又不会让正常输出产生可感知的延迟。
    private void EmitCallback(ReadOnlySpan<byte> p)
    {
        if (callback == null || _stopped) return;
        if (!stripTrailer)
        {
            CallbackChunks(p);
            return;
        }
        var pending = _pending;
        var merged = new byte[pending.Length + p.Length];
        pending.CopyTo(merged, 0);
        p.CopyTo(merged.AsSpan(pending.Length));
        _pending = merged;
        var index = IndexOf(_pending, _marker);
        if (index >= 0)
        {
            CallbackChunks(_pending.AsSpan(0, index));
            _pending = [];
            _stopped = true;
            return;
        }
        var keep = _marker.Length - 1;
        if (_pending.Length > keep)
        {
            var emit = _pending.Length - keep;
            CallbackChunks(_pending.AsSpan(0, emit));
            _pending = _pending[emit..];
        }
    }

    private void CallbackChunks(ReadOnlySpan<byte> data)
    {
        if (callback == null) return;
        while (data.Length > 0)
        {
            var n = Math.Min(data.Length, 4096);
            var chunk = data[..n].ToArray();
            callback(stream, chunk);
            data = data[n..];
        }
    }

    public void FinishCallback()
    {
        if (!_stopped && _pending.Length > 0)
        {
            CallbackChunks(_pending);
        }
        _pending = [];
    }

    public byte[] Captured()
    {
        var head = _buf.ToArray();
        var result = new byte[head.Length + _tail.Length];
        head.CopyTo(result, 0);
        _tail.CopyTo(result, head.Length);
        return result;
    }

    private static int IndexOf(byte[] data, byte[] marker)
    {
        if (marker.Length == 0 || marker.Length > data.Length) return -1;
        for (var i = 0; i <= data.Length - marker.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < marker.Length; j++)
            {
                if (data[i + j] != marker[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }
}
