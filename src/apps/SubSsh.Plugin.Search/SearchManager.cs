// SubSsh.Plugin.Search — 搜索引擎（对应 Go internal/searchx/search.go + helper.go + fallback.go）。
// Grep：远端 rg --json（exit 127 降级）；Find：fd/fdfind（含 --full-path 规则，exit 127 降级）。
// 降级顺序：helper（远端 ISettings.SearchHelper 指定的二进制，stdin JSON 协议）→ SFTP 本地遍历。
// 错误文案对齐 Go；30 秒搜索超时、64KiB stderr 截断、256KiB 输出字节预算。
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.Search;

public sealed class SearchManager : ISearchEngine
{
    private const int DefaultGrepLimit = 100;
    private const int MaxGrepLimit = 2000;
    private const int DefaultFindLimit = 1000;
    private const int MaxFindLimit = 5000;
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(30);
    private const int MaxStderr = 64 << 10;

    private const string GrepHelperWarning =
        "远端缺少 rg，已上传临时 SubSSH 搜索 helper 在远端本地遍历（Go 正则语义，运行结束后已自动删除）";
    private const string FindHelperWarning =
        "远端缺少 fd/fdfind，已上传临时 SubSSH 搜索 helper 在远端本地遍历（运行结束后已自动删除）";
    private const string GrepFallbackWarning =
        "远端缺少 rg，已使用 SFTP 降级搜索；大型目录上的性能可能低于原生 rg";
    private const string FindFallbackWarning =
        "远端缺少 fd/fdfind，已使用 SFTP 降级搜索；大型目录上的性能可能低于原生 fd";

    private readonly IConnectionPool _pool;
    private readonly ISettings _settings;

    private readonly object _mu = new();
    private readonly Dictionary<string, HelperState> _helper = new(StringComparer.Ordinal);

    public SearchManager(IConnectionPool pool, ISettings settings)
    {
        _pool = pool;
        _settings = settings;
    }

    public async Task<GrepResult> GrepAsync(string host, GrepOptions opt, CancellationToken ct = default)
    {
        if (opt.Pattern == "")
            throw new SubSshException("pattern 不能为空");
        if (opt.Pattern.Contains('\0') || opt.Path.Contains('\0') || opt.Glob.Contains('\0'))
            throw new SubSshException("搜索参数不能包含 NUL");
        if (opt.Path == "")
            opt = opt with { Path = "." };
        if (opt.Limit <= 0)
            opt = opt with { Limit = DefaultGrepLimit };
        if (opt.Limit > MaxGrepLimit)
            opt = opt with { Limit = MaxGrepLimit };
        var context = opt.Context < 0 ? 0 : opt.Context > 20 ? 20 : opt.Context;
        opt = opt with { Context = context };

        var args = new List<string> { "--json", "--line-number", "--color=never", "--hidden", "--max-columns=4000", "--max-columns-preview" };
        if (opt.IgnoreCase) args.Add("--ignore-case");
        if (opt.Literal) args.Add("--fixed-strings");
        if (opt.Glob != "") { args.Add("--glob"); args.Add(opt.Glob); }
        if (opt.Context > 0) { args.Add("--context"); args.Add(opt.Context.ToString()); }
        args.Add("--");
        args.Add(opt.Pattern);
        args.Add(opt.Path);
        var command = "command -v rg >/dev/null 2>&1 || { printf '%s\\n' 'ripgrep (rg) 未安装' >&2; exit 127; }; exec rg " + ShellArgs(args);

        var out_ = new GrepResult { Lines = new List<GrepLine>(Math.Min(opt.Limit, 128)), Engine = "rg" };
        var acc = new StreamAccumulator { OutputBytes = Encoding.UTF8.GetByteCount(out_.Engine) + 64 };
        var (stderr, exitCode, stopped) = await StreamAsync(host, command, line =>
        {
            if (!TryDecodeRgLine(line, out var @event) || (@event!.Type != "match" && @event.Type != "context"))
                return true;
            if (!TryGrepLine(@event, out var item))
                return true;
            item = item with { Path = RelativeResultPath(item.Path, opt.Path) };
            var lineBytes = SearchCore.GrepLineJsonBytes(item) + 1;
            if (acc.OutputBytes + lineBytes > SearchCoreLimits.MaxOutputBytes)
                return false;
            acc.OutputBytes += lineBytes;
            if (item.Match)
            {
                acc.MatchCount++;
                if (acc.MatchCount > opt.Limit)
                {
                    acc.MatchCount = opt.Limit;
                    return false;
                }
            }
            ((List<GrepLine>)out_.Lines).Add(item);
            return acc.MatchCount < opt.Limit;
        }, ct).ConfigureAwait(false);

        out_ = out_ with { Truncated = stopped || acc.MatchCount >= opt.Limit };
        if (exitCode == 127 && !stopped)
            return await GrepWithoutNativeAsync(host, opt, ct).ConfigureAwait(false);
        if (exitCode != 0 && exitCode != 1 && !stopped)
            throw CommandError("rg", exitCode, stderr);
        return out_;
    }

    public async Task<FindResult> FindAsync(string host, FindOptions opt, CancellationToken ct = default)
    {
        if (opt.Pattern == "")
            throw new SubSshException("pattern 不能为空");
        if (opt.Pattern.Contains('\0') || opt.Path.Contains('\0'))
            throw new SubSshException("搜索参数不能包含 NUL");
        if (opt.Path == "")
            opt = opt with { Path = "." };
        if (opt.Limit <= 0)
            opt = opt with { Limit = DefaultFindLimit };
        if (opt.Limit > MaxFindLimit)
            opt = opt with { Limit = MaxFindLimit };

        var pattern = opt.Pattern;
        var args = new List<string> { "--glob", "--color=never", "--hidden", "--max-results", opt.Limit.ToString() };
        if (pattern.Contains('/'))
        {
            args.Add("--full-path");
            if (!pattern.StartsWith('/') && !pattern.StartsWith("**/") && pattern != "**")
                pattern = "**/" + pattern;
        }
        args.Add("--");
        args.Add(pattern);
        args.Add(opt.Path);
        var command = "if command -v fd >/dev/null 2>&1; then _subssh_fd=fd; elif command -v fdfind >/dev/null 2>&1; then _subssh_fd=fdfind; else printf '%s\\n' 'fd 未安装' >&2; exit 127; fi; exec \"$_subssh_fd\" " + ShellArgs(args);

        var out_ = new FindResult { Paths = new List<string>(Math.Min(opt.Limit, 256)), Engine = "fd" };
        var acc = new StreamAccumulator { OutputBytes = Encoding.UTF8.GetByteCount(out_.Engine) + 64 };
        var (stderr, exitCode, stopped) = await StreamAsync(host, command, line =>
        {
            line = line.TrimEnd('\r');
            if (line == "")
                return true;
            var resultPath = RelativeResultPath(line, opt.Path);
            var pathBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(resultPath)) + 1;
            if (acc.OutputBytes + pathBytes > SearchCoreLimits.MaxOutputBytes)
                return false;
            acc.OutputBytes += pathBytes;
            ((List<string>)out_.Paths).Add(resultPath);
            return ((List<string>)out_.Paths).Count < opt.Limit;
        }, ct).ConfigureAwait(false);

        out_ = out_ with { Truncated = stopped || ((List<string>)out_.Paths).Count >= opt.Limit };
        if (exitCode == 127 && !stopped)
            return await FindWithoutNativeAsync(host, opt, ct).ConfigureAwait(false);
        if (exitCode != 0 && !stopped)
            throw CommandError("fd", exitCode, stderr);
        return out_;
    }

    // ------------------------------------------------------------------
    // 降级入口（对应 Go grepWithoutNative / findWithoutNative）。
    // ------------------------------------------------------------------
    private async Task<GrepResult> GrepWithoutNativeAsync(string host, GrepOptions opt, CancellationToken ct)
    {
        _ = GrepPatternCompiler.Compile(opt.Pattern, opt.IgnoreCase, opt.Literal); // 先校验（文案 Go 一致）
        _ = PathMatcher.Compile(opt.Glob, findPattern: false);                     // glob 无效抛 SubSshException
        var (result, handled) = await HelperGrepAsync(host, opt, ct).ConfigureAwait(false);
        if (handled)
            return result;
        var sftpResult = await SftpFallbackAsync(host, (fs, token) => SearchCore.GrepAsync(fs, opt, token), ct).ConfigureAwait(false);
        return sftpResult with { Engine = "sftp", Warning = GrepFallbackWarning };
    }

    private async Task<FindResult> FindWithoutNativeAsync(string host, FindOptions opt, CancellationToken ct)
    {
        _ = PathMatcher.Compile(opt.Pattern, findPattern: true); // glob 无效抛 SubSshException
        var (result, handled) = await HelperFindAsync(host, opt, ct).ConfigureAwait(false);
        if (handled)
            return result;
        var sftpResult = await SftpFallbackAsync(host, (fs, token) => SearchCore.FindAsync(fs, opt, token), ct).ConfigureAwait(false);
        return sftpResult with { Engine = "sftp", Warning = FindFallbackWarning };
    }

    // ------------------------------------------------------------------
    // helper 协议（对应 Go helper.go runHelperSearch；二进制路径来自 ISettings.SearchHelper，
    // 远端没有 Go 版的临时上传 staging——本实现的 helper 二进制由部署方预先放到该路径）。
    // 请求 JSON 经 base64 管道喂给远端 stdin（IRemoteSession 未暴露 Stdin，见 TODO）。
    // ------------------------------------------------------------------
    private async Task<(GrepResult Result, bool Handled)> HelperGrepAsync(string host, GrepOptions opt, CancellationToken ct)
    {
        var (resp, handled) = await RunHelperSearchAsync(host, new HelperRequest(SearchCoreLimits.ProtocolVersion, "grep", opt, null), ct).ConfigureAwait(false);
        if (!handled)
            return (new GrepResult(), false);
        var out_ = resp.Grep ?? new GrepResult();
        return (out_ with { Engine = "helper", Warning = GrepHelperWarning }, true);
    }

    private async Task<(FindResult Result, bool Handled)> HelperFindAsync(string host, FindOptions opt, CancellationToken ct)
    {
        var (resp, handled) = await RunHelperSearchAsync(host, new HelperRequest(SearchCoreLimits.ProtocolVersion, "find", null, opt), ct).ConfigureAwait(false);
        if (!handled)
            return (new FindResult(), false);
        var out_ = resp.Find ?? new FindResult();
        return (out_ with { Engine = "helper", Warning = FindHelperWarning }, true);
    }

    private async Task<(HelperResponse Resp, bool Handled)> RunHelperSearchAsync(string host, HelperRequest request, CancellationToken ct)
    {
        var helperPath = _settings.SearchHelper;
        if (helperPath == "")
            return (new HelperResponse(), false); // 未配置 helper：直接降级（对应 Go HelperEnabled=false）

        var (client, ok) = await ProbePlatformAsync(host, ct).ConfigureAwait(false);
        if (!ok)
            return (new HelperResponse(), false);

        var encoded = JsonSerializer.Serialize(request, HelperJson.Options);
        var stdout = new MemoryStream();
        int exitCode;
        try
        {
            exitCode = await RunHelperAsync(host, helperPath, Encoding.UTF8.GetBytes(encoded), stdout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            MarkHelperUnusable(host, client);
            return (new HelperResponse(), false);
        }
        if (exitCode != 0)
        {
            MarkHelperUnusable(host, client);
            return (new HelperResponse(), false);
        }
        HelperResponse resp;
        try
        {
            resp = JsonSerializer.Deserialize<HelperResponse>(stdout.ToArray(), HelperJson.Options) ?? new HelperResponse();
        }
        catch (JsonException)
        {
            MarkHelperUnusable(host, client);
            return (new HelperResponse(), false);
        }
        if (resp.Version != SearchCoreLimits.ProtocolVersion)
        {
            // Go CheckProtocolVersion 失败：标记不可用并降级（错误不外抛）。
            MarkHelperUnusable(host, client);
            return (new HelperResponse(), false);
        }
        if (resp.Error != "")
            throw new SubSshException(resp.Error);
        if (request.Op == "grep" && resp.Grep == null || request.Op == "find" && resp.Find == null)
        {
            MarkHelperUnusable(host, client);
            return (new HelperResponse(), false);
        }
        return (resp, true);
    }

    /// <summary>平台探测（对应 Go probePlatform：仅 linux + 已知 arch 才可用，按主机缓存）。</summary>
    private async Task<(IRemoteConnection Client, bool Ok)> ProbePlatformAsync(string host, CancellationToken ct)
    {
        var client = await _pool.GetAsync(host, ct).ConfigureAwait(false);
        lock (_mu)
        {
            if (_helper.TryGetValue(host, out var cached) && cached.Usable != null)
                return (client, cached.Usable.Value);
        }
        var lines = new List<string>(2);
        try
        {
            await StreamAsync(host, "uname -s && uname -m", line =>
            {
                if (lines.Count < 2) lines.Add(line.Trim());
                return true;
            }, ct, probeTimeout: TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            MarkPlatform(host, usable: false);
            return (client, false);
        }
        var usable = lines.Count >= 2 && NormalizePlatform(lines[0], lines[1]) != null;
        MarkPlatform(host, usable);
        return (client, usable);
    }

    private void MarkPlatform(string host, bool usable)
    {
        lock (_mu)
        {
            _helper[host] = new HelperState(usable);
        }
    }

    private static string? NormalizePlatform(string osName, string arch)
    {
        if (!string.Equals(osName.Trim(), "linux", StringComparison.OrdinalIgnoreCase))
            return null;
        return arch.Trim().ToLowerInvariant() switch
        {
            "x86_64" or "amd64" => "amd64",
            "aarch64" or "arm64" => "arm64",
            _ => null,
        };
    }

    private void MarkHelperUnusable(string host, IRemoteConnection client)
    {
        lock (_mu)
        {
            _helper[host] = new HelperState(false);
        }
    }

    /// <summary>运行 helper（对应 Go runHelper；请求经 base64 管道进 stdin，stdout 上限 4MiB）。</summary>
    private async Task<int> RunHelperAsync(string host, string remotePath, byte[] request, MemoryStream stdout, CancellationToken ct)
    {
        var b64 = Convert.ToBase64String(request);
        var command = "cd \"$HOME\" 2>/dev/null || exit 1; printf '%s' " + Shq(b64) + " | base64 -d | " + Shq(remotePath);
        var (stderr, exitCode, _) = await StreamAsync(host, command, line =>
        {
            var bytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (stdout.Length + bytes > (4 << 20))
                return false;
            stdout.Write(Encoding.UTF8.GetBytes(line));
            stdout.WriteByte((byte)'\n');
            return true;
        }, ct).ConfigureAwait(false);
        return exitCode;
    }

    // ------------------------------------------------------------------
    // SFTP 降级（对应 Go openSearchFS + grepSFTP/findSFTP）。
    // ------------------------------------------------------------------
    private async Task<T> SftpFallbackAsync<T>(string host, Func<IRemoteSftp, CancellationToken, Task<T>> core, CancellationToken ct)
    {
        var client = await _pool.GetAsync(host, ct).ConfigureAwait(false);
        await using var fs = await client.OpenSftpAsync(ct).ConfigureAwait(false);
        return await core(fs, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // 远端流式执行（对应 Go Manager.stream；返回 (stderr, exitCode, stopped)）。
    // ------------------------------------------------------------------
    private sealed class StreamAccumulator
    {
        public int OutputBytes { get; set; }
        public int MatchCount { get; set; }
    }

    private async Task<(string Stderr, int ExitCode, bool Stopped)> StreamAsync(
        string host, string command, Func<string, bool> consume, CancellationToken ct, TimeSpan? probeTimeout = null)
    {
        var client = await _pool.GetAsync(host, ct).ConfigureAwait(false);
        await using var session = await client.OpenSessionAsync(ct).ConfigureAwait(false);
        await session.StartAsync("cd \"$HOME\" 2>/dev/null || exit 1; " + command, ct).ConfigureAwait(false);

        var stderrTask = ReadStderrAsync(session.Stderr);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(probeTimeout ?? SearchTimeout);
        // 超时/取消时终止远端命令（对应 Go ctx.Done → session.Close）。
        await using var reg = timeoutCts.Token.Register(() =>
        {
            try { session.KillAsync().GetAwaiter().GetResult(); } catch { /* 已关闭 */ }
        });

        var stopped = false;
        using var reader = new StreamReader(session.Stdout, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 65536, leaveOpen: true);
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false)) != null)
            {
                if (!consume(line))
                {
                    stopped = true;
                    try { await session.KillAsync(ct).ConfigureAwait(false); } catch { /* 已关闭 */ }
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 搜索超时（对应 Go nativeCtx 超时返回 ctx.Err()）。
            throw;
        }

        int? exitCodeRaw = null;
        try
        {
            exitCodeRaw = await session.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (!stopped)
                throw;
        }
        var stderr = await stderrTask.ConfigureAwait(false);
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        var exitCode = exitCodeRaw ?? (stopped ? 0 : throw new SubSshException($"搜索命令未返回退出码: {stderr.Trim()}"));
        return (stderr, exitCode, stopped);
    }

    private static async Task<string> ReadStderrAsync(Stream stderr)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stderr.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxStderr)
                break;
        }
        var data = buffer.ToArray();
        if (data.Length > MaxStderr)
            data = [.. data[..MaxStderr], .. Encoding.UTF8.GetBytes("\n[stderr 已截断]")];
        return Encoding.UTF8.GetString(data);
    }

    // ------------------------------------------------------------------
    // 小工具（对齐 Go shellArgs/relativeResultPath/commandError/rg 解析）。
    // ------------------------------------------------------------------
    /// <summary>对应 Go strings.TrimSuffix(TrimSuffix(s,"\n"),"\r")：各去掉一个换行后缀。</summary>
    private static string TrimSuffixNewline(string s)
    {
        if (s.EndsWith('\n')) s = s[..^1];
        if (s.EndsWith('\r')) s = s[..^1];
        return s;
    }

    /// <summary>单引号安全转义（对齐 Go execx.SHQ）。</summary>
    private static string Shq(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string ShellArgs(IReadOnlyList<string> args)
    {
        var quoted = new string[args.Count];
        for (var i = 0; i < args.Count; i++)
            quoted[i] = Shq(args[i]);
        return string.Join(" ", quoted);
    }

    /// <summary>结果路径相对化（对应 Go relativeResultPath）。</summary>
    private static string RelativeResultPath(string result, string root)
    {
        result = PosixPath.Clean(result);
        root = PosixPath.Clean(root);
        if (root == ".")
            return result.StartsWith("./", StringComparison.Ordinal) ? result[2..] : result;
        if (result == root)
            return PosixPath.Base(result);
        var prefix = root.EndsWith('/') ? root : root + "/";
        return result.StartsWith(prefix, StringComparison.Ordinal) ? result[prefix.Length..] : result;
    }

    private static SubSshException CommandError(string command, int exitCode, string stderr)
    {
        stderr = stderr.Trim();
        if (stderr == "")
            stderr = "无错误输出";
        return new SubSshException($"{command} 退出码 {exitCode}: {stderr}");
    }

    private static bool TryDecodeRgLine(string line, out RgEvent? @event)
    {
        try
        {
            @event = JsonSerializer.Deserialize<RgEvent>(line);
            return @event != null;
        }
        catch (JsonException)
        {
            @event = null;
            return false;
        }
    }

    private static bool TryGrepLine(RgEvent @event, out GrepLine item)
    {
        var path = TryRgString(@event.Data.Path);
        if (path == null || @event.Data.LineNumber == null)
        {
            item = new GrepLine();
            return false;
        }
        var text = TryRgString(@event.Data.Lines);
        if (text == null)
        {
            item = new GrepLine();
            return false;
        }
        text = TrimSuffixNewline(text);
        var match = @event.Type == "match";
        long column = 0;
        if (match && @event.Data.Submatches.Length > 0)
            column = @event.Data.Submatches[0].Start + 1;
        item = new GrepLine { Path = path, Line = @event.Data.LineNumber.Value, Column = column, Text = text, Match = match };
        return true;
    }

    private static string? TryRgString(RgText value)
    {
        if (value.Text != null)
            return value.Text;
        if (value.Bytes == null)
            return null;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value.Bytes));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed record HelperState(bool? Usable);

    // ---- rg --json 事件模型（对应 Go rgEvent/rgText） ----
    private sealed record RgText([property: JsonPropertyName("text")] string? Text, [property: JsonPropertyName("bytes")] string? Bytes);
    private sealed record RgSubmatch([property: JsonPropertyName("start")] int Start);
    private sealed record RgEventData(
        [property: JsonPropertyName("path")] RgText Path,
        [property: JsonPropertyName("lines")] RgText Lines,
        [property: JsonPropertyName("line_number")] int? LineNumber,
        [property: JsonPropertyName("submatches")] RgSubmatch[] Submatches);
    private sealed record RgEvent([property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("data")] RgEventData Data);
}

/// <summary>helper 协议模型（对应 Go searchcore HelperRequest/HelperResponse，camelCase 与 Go json tag 一致）。</summary>
public sealed record HelperRequest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("grep")] GrepOptions? Grep,
    [property: JsonPropertyName("find")] FindOptions? Find);

public sealed record HelperResponse(
    [property: JsonPropertyName("version")] int Version = 0,
    [property: JsonPropertyName("grep")] GrepResult? Grep = null,
    [property: JsonPropertyName("find")] FindResult? Find = null,
    [property: JsonPropertyName("error")] string Error = "");

public static class HelperJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
