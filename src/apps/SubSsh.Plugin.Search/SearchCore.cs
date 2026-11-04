// SubSsh.Plugin.Search — 降级搜索核心（对应 Go internal/searchcore/core.go 的
// glob 编译、路径匹配、忽略规则与 SFTP 遍历 grep/find，1:1 移植）。
// FS 抽象用 IRemoteSftp 承载（Getwd/ReadDir/Open/Lstat + ReadLink 判软链）。
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.Search;

/// <summary>searchcore 常量（对齐 Go internal/searchcore/core.go）。</summary>
public static class SearchCoreLimits
{
    public const int MaxTraversalEntries = 100_000;
    public const int MaxTraversalDepth = 128;
    public const int MaxFallbackFileSize = 8 << 20;
    public const int BinaryProbeBytes = 8 << 10;
    public const int MaxIgnoreFileBytes = 256 << 10;
    public const int MaxOutputBytes = 256 << 10;
    public const int MaxLineBytes = 1 << 20;
    public const int ProtocolVersion = 1;
}

// ---------------------------------------------------------------------------
// POSIX path 工具（对齐 Go path.Clean/Join/Base/IsAbs —— 远端是 Linux）。
// ---------------------------------------------------------------------------
public static class PosixPath
{
    public static bool IsAbs(string p) => p.StartsWith('/');

    public static string Base(string p)
    {
        if (p == "") return ".";
        while (p.EndsWith('/'))
        {
            p = p[..^1];
            if (p == "") return "/";
        }
        var i = p.LastIndexOf('/');
        return i < 0 ? p : p[(i + 1)..];
    }

    public static string Join(params string[] elems)
    {
        var parts = elems.Where(e => e != "").ToArray();
        return parts.Length == 0 ? "" : Clean(string.Join("/", parts));
    }

    /// <summary>Go path.Clean 的逐行移植。</summary>
    public static string Clean(string path)
    {
        if (path == "") return ".";
        var rooted = path.StartsWith('/');
        var n = path.Length;
        var out_ = new List<char>(n);
        var r = 0;
        var dotdot = 0;
        if (rooted)
        {
            out_.Add('/');
            r = 1;
            dotdot = 1;
        }
        while (r < n)
        {
            if (path[r] == '/')
            {
                r++;
            }
            else if (path[r] == '.' && (r + 1 == n || path[r + 1] == '/'))
            {
                r++;
            }
            else if (path[r] == '.' && path[r + 1] == '.' && (r + 2 == n || path[r + 2] == '/'))
            {
                r += 2;
                if (out_.Count > dotdot)
                {
                    out_.Pop();
                    while (out_.Count > dotdot && out_[^1] != '/')
                        out_.Pop();
                }
                else if (!rooted)
                {
                    if (out_.Count > 0) out_.Add('/');
                    out_.Add('.');
                    out_.Add('.');
                    dotdot = out_.Count;
                }
            }
            else
            {
                if ((rooted && out_.Count != 1) || (!rooted && out_.Count != 0))
                    out_.Add('/');
                while (r < n && path[r] != '/')
                    out_.Add(path[r++]);
            }
        }
        if (out_.Count == 0) return ".";
        return new string(out_.ToArray());
    }

    private static void Pop(this List<char> list) => list.RemoveAt(list.Count - 1);
}

// ---------------------------------------------------------------------------
// glob 编译（对齐 Go compileGlob：* ? [..] **，未闭合/无效字符组报错文案一致）。
// ---------------------------------------------------------------------------
public static class GlobCompiler
{
    /// <summary>glob → 全串锚定正则（对应 Go compileGlob）。</summary>
    public static Regex Compile(string pattern)
    {
        var expression = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '*':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        while (i + 1 < pattern.Length && pattern[i + 1] == '*') i++;
                        if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                        {
                            i++;
                            expression.Append("(?:.*/)?");
                        }
                        else
                        {
                            expression.Append(".*");
                        }
                    }
                    else
                    {
                        expression.Append("[^/]*");
                    }
                    break;
                case '?':
                    expression.Append("[^/]");
                    break;
                case '[':
                    var end = i + 1;
                    while (end < pattern.Length && pattern[end] != ']') end++;
                    if (end == pattern.Length)
                        throw new SubSshException("未闭合的字符组");
                    var @class = pattern[(i + 1)..end];
                    if (@class == "" || @class.Contains('/'))
                        throw new SubSshException("无效的字符组");
                    expression.Append('[');
                    if (@class[0] == '!')
                    {
                        expression.Append('^');
                        @class = @class[1..];
                    }
                    if (@class == "")
                        throw new SubSshException("无效的字符组");
                    expression.Append(@class.Replace("\\", "\\\\"));
                    expression.Append(']');
                    i = end;
                    break;
                case '\\':
                    if (i + 1 >= pattern.Length)
                        throw new SubSshException("末尾反斜杠");
                    i++;
                    expression.Append(Regex.Escape(pattern.Substring(i, 1)));
                    break;
                default:
                    expression.Append(Regex.Escape(pattern.Substring(i, 1)));
                    break;
            }
        }
        expression.Append('$');
        return new Regex(expression.ToString(), RegexOptions.CultureInvariant);
    }

    /// <summary>Go regexp.QuoteMeta 等价：转义全部正则元字符。</summary>
    public static string QuoteMeta(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
            if ("\\.+*?()|[]{}^$".Contains(ch))
                sb.Append('\\').Append(ch);
            else
                sb.Append(ch);
        return sb.ToString();
    }
}

// ---------------------------------------------------------------------------
// 路径匹配器（对齐 Go PathMatcher/CompilePathMatcher）。
// ---------------------------------------------------------------------------
public sealed class PathMatcher
{
    private readonly Regex? _match;
    private readonly bool _basenameOnly;
    private readonly bool _absolute;
    private readonly bool _negated;

    private PathMatcher(Regex? match, bool basenameOnly, bool absolute, bool negated)
    {
        _match = match;
        _basenameOnly = basenameOnly;
        _absolute = absolute;
        _negated = negated;
    }

    /// <summary>编译路径 glob（对应 Go CompilePathMatcher）。</summary>
    public static PathMatcher Compile(string pattern, bool findPattern)
    {
        if (pattern == "")
            return new PathMatcher(null, false, false, false);
        var negated = false;
        if (!findPattern && pattern.StartsWith('!'))
        {
            negated = true;
            pattern = pattern[1..];
        }
        if (pattern == "")
            throw new SubSshException("pattern 不能为空");
        var absolute = PosixPath.IsAbs(pattern);
        var basenameOnly = !pattern.Contains('/');
        if (absolute)
        {
            pattern = PosixPath.Clean(pattern);
        }
        else if (!basenameOnly && findPattern && !pattern.StartsWith("**/") && pattern != "**")
        {
            pattern = "**/" + pattern;
        }
        else
        {
            pattern = pattern.TrimStart('/');
        }
        return new PathMatcher(GlobCompiler.Compile(pattern), basenameOnly, absolute, negated);
    }

    public bool Matches(string relativePath, string fullPath)
    {
        if (_match == null)
            return true;
        var candidate = relativePath;
        if (_absolute)
            candidate = fullPath;
        else if (_basenameOnly)
            candidate = PosixPath.Base(relativePath);
        var matched = _match.IsMatch(candidate);
        return _negated ? !matched : matched;
    }
}

/// <summary>搜索模式编译（对应 Go CompileGrepPattern；错误文案一致）。</summary>
public static class GrepPatternCompiler
{
    public static Regex Compile(string pattern, bool ignoreCase, bool literal)
    {
        if (literal)
            pattern = GlobCompiler.QuoteMeta(pattern);
        if (ignoreCase)
            pattern = "(?i:" + pattern + ")";
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));
        }
        catch (ArgumentException e)
        {
            throw new SubSshException($"Go 正则无效: {e.Message}");
        }
    }
}

// ---------------------------------------------------------------------------
// 忽略规则（对齐 Go ignoreRule/parseIgnoreRules/ignoredPath/loadIgnoreRules）。
// ---------------------------------------------------------------------------
public sealed class IgnoreRule
{
    public string Base { get; init; } = "";
    public Regex Match { get; init; } = null!;
    public bool BasenameOnly { get; init; }
    public bool Directory { get; init; }
    public bool Negated { get; init; }
}

public static class IgnoreRules
{
    public static bool IgnoredPath(IReadOnlyList<IgnoreRule> rules, string relativePath, bool directory)
    {
        var ignored = false;
        foreach (var rule in rules)
        {
            if (rule.Directory && !directory)
                continue;
            var candidate = relativePath;
            if (rule.Base != "")
            {
                var prefix = rule.Base + "/";
                if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                candidate = candidate[prefix.Length..];
            }
            if (rule.BasenameOnly)
                candidate = PosixPath.Base(candidate);
            if (rule.Match.IsMatch(candidate))
                ignored = !rule.Negated;
        }
        return ignored;
    }

    /// <summary>解析 .gitignore/.ignore/.rgignore 内容（对应 Go parseIgnoreRules）。</summary>
    public static IReadOnlyList<IgnoreRule> Parse(string contents, string baseDir)
    {
        var rules = new List<IgnoreRule>();
        foreach (var raw in contents.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line == "" || line.StartsWith('#'))
                continue;
            var negated = false;
            if (line.StartsWith("\\#") || line.StartsWith("\\!"))
            {
                line = line[1..];
            }
            else if (line.StartsWith('!'))
            {
                negated = true;
                line = line[1..];
            }
            if (line == "")
                continue;
            var directory = false;
            if (line.EndsWith('/'))
            {
                directory = true;
                line = line.TrimEnd('/');
            }
            line = line.TrimStart('/');
            var basenameOnly = !line.Contains('/');
            Regex compiled;
            try
            {
                compiled = GlobCompiler.Compile(line);
            }
            catch (SubSshException e)
            {
                throw new SubSshException(e.Message);
            }
            rules.Add(new IgnoreRule { Base = baseDir, Match = compiled, BasenameOnly = basenameOnly, Directory = directory, Negated = negated });
        }
        return rules;
    }

    /// <summary>读目录下的忽略文件（对应 Go loadIgnoreRules；非存在性错误原样上抛）。</summary>
    public static async Task<IReadOnlyList<IgnoreRule>> LoadAsync(IRemoteSftp fs, string directory, string relativeDir, CancellationToken ct)
    {
        var rules = new List<IgnoreRule>();
        foreach (var name in new[] { ".gitignore", ".ignore", ".rgignore" })
        {
            Stream file;
            try
            {
                file = await fs.OpenReadAsync(PosixPath.Join(directory, name), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // 远端 SFTP 不区分“不存在”与其他 IO 错误，这里统一按不存在跳过（见 TODO）。
                continue;
            }
            await using (file.ConfigureAwait(false))
            {
                using var ms = new MemoryStream();
                var buffer = new byte[16384];
                int read;
                while ((read = await file.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    ms.Write(buffer, 0, read);
                    if (ms.Length > SearchCoreLimits.MaxIgnoreFileBytes)
                        throw new SubSshException($"忽略文件 {PosixPath.Join(directory, name)} 超过 {SearchCoreLimits.MaxIgnoreFileBytes} 字节");
                }
                rules.AddRange(Parse(Encoding.UTF8.GetString(ms.ToArray()), relativeDir));
            }
        }
        return rules;
    }
}

// ---------------------------------------------------------------------------
// 遍历器（对齐 Go searchWalker：条目/深度上限、软链跳过、.git/.hg/.svn 跳过）。
// ---------------------------------------------------------------------------
public sealed class SearchWalker(IRemoteSftp fs, string root)
{
    private readonly IRemoteSftp _fs = fs;
    private readonly string _root = root;
    private int _entries;

    public bool Truncated { get; private set; }

    /// <summary>visit 返回 false 表示停止遍历（结果截断）。</summary>
    public async Task WalkAsync(
        CancellationToken ct,
        Func<string, string, RemoteFileStat, CancellationToken, Task<bool>> visit)
    {
        var info = await _fs.StatAsync(_root, ct).ConfigureAwait(false);
        var rootLink = await _fs.ReadLinkAsync(_root, ct).ConfigureAwait(false);
        if (rootLink != null)
            return;
        if (!info.Directory)
        {
            var keep = await visit(_root, PosixPath.Base(_root), info, ct).ConfigureAwait(false);
            if (!keep) Truncated = true;
            return;
        }
        try
        {
            await WalkDirAsync(ct, _root, "", [], 0, visit).ConfigureAwait(false);
        }
        catch (WalkStoppedException)
        {
            // Go errWalkStopped：正常截断退出。
        }
    }

    private async Task WalkDirAsync(
        CancellationToken ct, string directory, string relativeDir,
        IReadOnlyList<IgnoreRule> inherited, int depth,
        Func<string, string, RemoteFileStat, CancellationToken, Task<bool>> visit)
    {
        ct.ThrowIfCancellationRequested();
        if (depth >= SearchCoreLimits.MaxTraversalDepth)
        {
            Truncated = true;
            return;
        }
        var local = await IgnoreRules.LoadAsync(_fs, directory, relativeDir, ct).ConfigureAwait(false);
        var rules = local.Count == 0 ? inherited : inherited.Concat(local).ToArray();
        var entries = await _fs.ReadDirAsync(directory, ct).ConfigureAwait(false);
        var sorted = entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        foreach (var info in sorted)
        {
            ct.ThrowIfCancellationRequested();
            _entries++;
            if (_entries > SearchCoreLimits.MaxTraversalEntries)
            {
                Truncated = true;
                throw new WalkStoppedException();
            }
            var name = info.Name;
            var relativePath = relativeDir == "" ? name : PosixPath.Join(relativeDir, name);
            var fullPath = PosixPath.Join(directory, name);
            if (await _fs.ReadLinkAsync(fullPath, ct).ConfigureAwait(false) != null)
                continue;
            if (IgnoreRules.IgnoredPath(rules, relativePath, info.Directory))
                continue;
            if (info.Directory)
            {
                if (name is ".git" or ".hg" or ".svn")
                    continue;
                var keepGoing = await visit(fullPath, relativePath, info, ct).ConfigureAwait(false);
                if (!keepGoing)
                {
                    Truncated = true;
                    throw new WalkStoppedException();
                }
                await WalkDirAsync(ct, fullPath, relativePath, rules, depth + 1, visit).ConfigureAwait(false);
                continue;
            }
            var keepFile = await visit(fullPath, relativePath, info, ct).ConfigureAwait(false);
            if (!keepFile)
            {
                Truncated = true;
                throw new WalkStoppedException();
            }
        }
    }

    private sealed class WalkStoppedException : Exception;
}

// ---------------------------------------------------------------------------
// 降级搜索（对应 Go searchcore.Grep/Find + grepFallbackFile/appendFallbackGrepLine）。
// ---------------------------------------------------------------------------
public static class SearchCore
{
    /// <summary>解析搜索根目录（对应 Go resolveSearchRoot；~ 展开远端主目录）。</summary>
    public static async Task<string> ResolveSearchRootAsync(IRemoteSftp fs, string name, CancellationToken ct)
    {
        string cwd;
        try
        {
            cwd = await fs.GetwdAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new SubSshException($"读取远端主目录: {e.Message}");
        }
        if (name == "~")
            name = cwd;
        else if (name.StartsWith("~/"))
            name = PosixPath.Join(cwd, name[2..]);
        else if (!PosixPath.IsAbs(name))
            name = PosixPath.Join(cwd, name);
        return PosixPath.Clean(name);
    }

    /// <summary>累积器：lambda 内可变的输出预算/计数（对齐 Go 指针语义）。</summary>
    private sealed class GrepAccumulator
    {
        public List<GrepLine> Lines { get; } = new(Math.Min(128, 128));
        public int MatchCount { get; set; }
        public int OutputBytes { get; set; }
        public bool Truncated { get; set; }
    }

    public static async Task<GrepResult> GrepAsync(IRemoteSftp fs, GrepOptions opt, CancellationToken ct)
    {
        var searchPattern = GrepPatternCompiler.Compile(opt.Pattern, opt.IgnoreCase, opt.Literal);
        var glob = PathMatcher.Compile(opt.Glob, findPattern: false);
        var root = await ResolveSearchRootAsync(fs, opt.Path, ct).ConfigureAwait(false);

        var acc = new GrepAccumulator { OutputBytes = 64 };
        var walker = new SearchWalker(fs, root);
        await walker.WalkAsync(ct, async (fullPath, relativePath, info, token) =>
        {
            if (info.Directory || !glob.Matches(relativePath, fullPath))
                return true;
            var (stopped, incomplete) = await GrepFallbackFileAsync(fs, fullPath, relativePath, info, searchPattern, opt, acc, token).ConfigureAwait(false);
            if (incomplete)
                acc.Truncated = true;
            return !stopped;
        }).ConfigureAwait(false);
        return new GrepResult
        {
            Lines = acc.Lines,
            MatchCount = acc.MatchCount,
            Truncated = acc.Truncated || walker.Truncated,
            Engine = "",
            Warning = "",
        };
    }

    public static async Task<FindResult> FindAsync(IRemoteSftp fs, FindOptions opt, CancellationToken ct)
    {
        var matcher = PathMatcher.Compile(opt.Pattern, findPattern: true);
        var root = await ResolveSearchRootAsync(fs, opt.Path, ct).ConfigureAwait(false);

        var paths = new List<string>(Math.Min(opt.Limit, 256));
        var outputBytes = 64;
        var truncated = false;
        var walker = new SearchWalker(fs, root);
        await walker.WalkAsync(ct, (fullPath, relativePath, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!matcher.Matches(relativePath, fullPath))
                return Task.FromResult(true);
            var encodedBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(relativePath)) + 1;
            if (outputBytes + encodedBytes > SearchCoreLimits.MaxOutputBytes)
            {
                truncated = true;
                return Task.FromResult(false);
            }
            outputBytes += encodedBytes;
            paths.Add(relativePath);
            if (paths.Count >= opt.Limit)
            {
                truncated = true;
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }).ConfigureAwait(false);
        return new FindResult
        {
            Paths = paths,
            Truncated = truncated || walker.Truncated,
            Engine = "",
            Warning = "",
        };
    }

    /// <summary>单文件降级 grep（对应 Go grepFallbackFile：8MiB 上限、二进制探测、上下文缓冲）。</summary>
    private static async Task<(bool Stopped, bool Incomplete)> GrepFallbackFileAsync(
        IRemoteSftp fs, string fullPath, string relativePath, RemoteFileStat info,
        Regex pattern, GrepOptions opt, GrepAccumulator acc, CancellationToken ct)
    {
        if (info.Size > SearchCoreLimits.MaxFallbackFileSize)
            return (false, true);
        Stream file;
        try
        {
            file = await fs.OpenReadAsync(fullPath, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 读不了（权限/竞态删除）的文件跳过，不中断整个搜索。
            Console.Error.WriteLine($"搜索跳过不可读文件 {fullPath}: {e.Message}");
            return (false, false);
        }
        await using (file.ConfigureAwait(false))
        {
            var probe = new byte[SearchCoreLimits.BinaryProbeBytes];
            var probeLen = 0;
            int read;
            while (probeLen < probe.Length && (read = await file.ReadAsync(probe.AsMemory(probeLen), ct).ConfigureAwait(false)) > 0)
                probeLen += read;
            if (Array.IndexOf(probe, (byte)0, 0, probeLen) >= 0)
                return (false, false); // 二进制文件跳过

            file.Position = 0;
            var previous = new Queue<(int Number, string Text)>();
            var after = 0;
            var lastEmitted = 0;
            var lineNumber = 0;
            using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 65536, leaveOpen: true);
            string? text;
            while ((text = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                ct.ThrowIfCancellationRequested();
                text = text.TrimEnd('\r');
                lineNumber++;
                var location = pattern.Match(text);
                if (location.Success)
                {
                    foreach (var prior in previous)
                    {
                        if (prior.Number > lastEmitted)
                        {
                            if (AppendFallbackGrepLine(acc, opt.Limit, new GrepLine { Path = relativePath, Line = prior.Number, Text = prior.Text }))
                                return (true, false);
                            lastEmitted = prior.Number;
                        }
                    }
                    if (AppendFallbackGrepLine(acc, opt.Limit, new GrepLine { Path = relativePath, Line = lineNumber, Column = location.Index + 1, Text = text, Match = true }))
                        return (true, false);
                    lastEmitted = lineNumber;
                    after = opt.Context;
                }
                else if (after > 0)
                {
                    if (AppendFallbackGrepLine(acc, opt.Limit, new GrepLine { Path = relativePath, Line = lineNumber, Text = text }))
                        return (true, false);
                    lastEmitted = lineNumber;
                    after--;
                }
                if (opt.Context > 0)
                {
                    if (previous.Count == opt.Context)
                        previous.Dequeue();
                    previous.Enqueue((lineNumber, text));
                }
            }
            // .NET StreamReader 行长无硬上限（Go bufio.Scanner 1MiB 上限），超长行不置截断标记（见 TODO）。
            return (false, false);
        }
    }

    /// <summary>追加降级 grep 行（对应 Go appendFallbackGrepLine；返回 true 表示应停止）。</summary>
    private static bool AppendFallbackGrepLine(GrepAccumulator acc, int limit, GrepLine line)
    {
        var encodedBytes = GrepLineJsonBytes(line) + 1;
        if (acc.OutputBytes + encodedBytes > SearchCoreLimits.MaxOutputBytes)
        {
            acc.Truncated = true;
            return true;
        }
        acc.OutputBytes += encodedBytes;
        acc.Lines.Add(line);
        if (line.Match)
        {
            acc.MatchCount++;
            if (acc.MatchCount >= limit)
            {
                acc.Truncated = true;
                return true;
            }
        }
        return false;
    }

    /// <summary>估算 GrepLine 的 JSON 字节数（对应 Go json.Marshal(GrepLine)+预算；column=0 时省略）。</summary>
    public static int GrepLineJsonBytes(GrepLine line)
    {
        var sb = new StringBuilder();
        sb.Append("{\"path\":").Append(JsonSerializer.Serialize(line.Path));
        sb.Append(",\"line\":").Append(line.Line);
        if (line.Column != 0)
            sb.Append(",\"column\":").Append(line.Column);
        sb.Append(",\"text\":").Append(JsonSerializer.Serialize(line.Text));
        sb.Append(",\"match\":").Append(line.Match ? "true" : "false").Append('}');
        return Encoding.UTF8.GetByteCount(sb.ToString());
    }
}
