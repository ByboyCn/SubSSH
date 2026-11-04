// 用户可见的远程命令执行记录（对应 mcpserver/command_runs.go）。
// startCommandRun：建记录 + 发布 command_started；finishCommandRun：计算状态并可靠落最终态
// （使用后台 context，不用已取消的请求 context）+ 发布 command_finished；preview 64 KiB 首尾截断。
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SubSsh.Core.Abstractions;
using SubSsh.Core.Events;

namespace SubSsh.Plugin.McpCore;

/// <summary>事件载荷（对应 Go events.Publish("command_started"/"command_finished"/"command_output") 的 map）。</summary>
public sealed record CommandStartedEvent(string RunId, string Tool, string Host, string Command, string Cwd, string Status, long StartedAt);
public sealed record CommandFinishedEvent(string RunId, string Tool, string Host, string Status, long? ExitCode, long StdoutBytes, long StderrBytes, bool OutputAvailable, long FinishedAt);

public static class CommandRuns
{
    private const int CommandPreviewLimit = 64 << 10;

    public static async Task<CommandRun> StartCommandRunAsync(
        IOneSshStoreFull store, EventBus bus, McpPrincipal? principal,
        string tool, HostConfig host, string command, string cwd, string session, CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var run = new CommandRun
        {
            Id = Guid.NewGuid().ToString("D"),
            Tool = tool,
            HostId = host.Id != 0 ? host.Id : null,
            Host = host.Name,
            Command = command,
            Cwd = cwd,
            Session = session.Length > 0 ? session : null,
            Status = "running",
            StartedAt = now,
            TokenId = principal != null && principal.Token.Id != 0 ? principal.Token.Id : null,
            TokenName = principal != null && principal.Token.Id != 0 ? principal.Token.Name : null,
        };
        await store.CreateCommandRunAsync(run, ct).ConfigureAwait(false);
        bus.Publish(new CommandStartedEvent(run.Id, tool, host.Name, command, cwd, "running", now));
        return run;
    }

    /// <summary>命令状态机（对应 Go commandRunStatus）。</summary>
    public static string RunStatus(ExecResult result, Exception? runErr, CancellationToken requestCt)
    {
        if (result.Timeout)
            return requestCt.IsCancellationRequested ? "cancelled" : "timeout";
        if (runErr != null)
        {
            // Runner 可能在远端已退出后才因 artifact/输出落盘失败返回错误；
            // ExitCodeKnown 独立表达 0 在内的真实退出码，不能靠数值正负猜测。
            if (result.ExitCodeKnown) return "failed";
            if (requestCt.IsCancellationRequested) return "cancelled";
            return "failed";
        }
        return result.ExitCode != 0 ? "failed" : "succeeded";
    }

    public static async Task FinishCommandRunAsync(
        IOneSshStoreFull store, EventBus bus, CommandRun run, ExecResult result, Exception? runErr, CancellationToken requestCt)
    {
        var status = RunStatus(result, runErr, requestCt);
        long? exitCode = status is "cancelled" or "timeout"
            ? null
            : result.ExitCode >= 0 && (runErr == null || result.ExitCodeKnown) ? result.ExitCode : null;
        var finish = new CommandRunFinish
        {
            Status = status,
            ExitCode = exitCode,
            StdoutPreview = Preview(result.Stdout),
            StderrPreview = Preview(result.Stderr),
            StdoutBytes = result.StdoutBytes,
            StderrBytes = result.StderrBytes,
            OutputAvailable = result.OutputRecorded,
            OutputError = result.OutputCaptureError,
            ErrorText = runErr?.Message,
            FinishedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
        };
        // 请求被取消时仍要可靠落最终状态，不使用可能已取消的请求 context。
        await store.FinishCommandRunAsync(run.Id, finish).ConfigureAwait(false);
        bus.Publish(new CommandFinishedEvent(run.Id, run.Tool, run.Host, status, finish.ExitCode,
            finish.StdoutBytes, finish.StderrBytes, finish.OutputAvailable, finish.FinishedAt));
    }

    /// <summary>64 KiB 首尾预览，中间省略提示（对应 Go commandPreview；按 UTF-16 码元截断，TODO：代理对安全回退）。</summary>
    private static string Preview(string value)
    {
        if (value.Length <= CommandPreviewLimit) return value;
        var head = CommandPreviewLimit / 2;
        var tailStart = value.Length - (CommandPreviewLimit - head);
        return value[..head] + "\n\n… 中间输出已省略，完整内容请在命令详情中读取 …\n\n" + value[tailStart..];
    }
}

/// <summary>单次 tools/call 的审计附加信息（run_ids / exit_code），经 AsyncLocal 从工具实现回传审计包装。</summary>
public static class AuditExtras
{
    private static readonly AsyncLocal<Extras?> Current = new();

    public sealed class Extras
    {
        public List<string> RunIds { get; } = [];
        public long? ExitCode { get; set; }
        public bool ExitCodeSet { get; set; }
    }

    public static Extras Scope => Current.Value ??= new Extras();

    public static Extras? Peek => Current.Value;
}

/// <summary>实时事件载荷（对应 Go events.Publish("tool_call")）。</summary>
public sealed record ToolCallEvent(string Tool, string? Host, bool Ok, long DurationMs, string? Summary);

public static class Audit
{
    /// <summary>参数脱敏（对应 Go redactedJSON/redact）：password → <redacted>；content/edits → <len=N>。
    /// TODO：改为遍历 JsonElement 树重建，避免正则对嵌套结构的近似。</summary>
    public static string RedactArgs(JsonElement args)
    {
        var raw = args.GetRawText();
        raw = Regex.Replace(raw, "\"password\"\\s*:\\s*\"(?:[^\"\\\\]|\\\\.)*\"", "\"password\":\"<redacted>\"");
        raw = Regex.Replace(raw, "\"(content|edits)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", m =>
        {
            var decoded = JsonSerializer.Deserialize<string>($"\"{m.Groups[2].Value}\"");
            return $"\"{m.Groups[1].Value}\":\"<len={Encoding.UTF8.GetByteCount(decoded ?? "")}>\"";
        });
        return raw;
    }

    /// <summary>hostOf（对应 Go hostOf）：入参里 host / name 字段非空字符串即为 host。</summary>
    public static string? HostOf(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "host", "name" })
            if (args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        return null;
    }

    /// <summary>活动流短摘要（对应 Go callSummary/summarizeParams/truncateSummary，240 字符上限）。</summary>
    public static string? CallSummary(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        var command = Str(args, "command");
        if (command.Length > 0) return Truncate(command);
        var path = Str(args, "path");
        var pattern = Str(args, "pattern");
        if (path.Length > 0 && pattern.Length > 0) return Truncate(pattern + "  " + path);
        if (path.Length > 0) return Truncate(path);
        var src = Str(args, "src_path");
        var dst = Str(args, "dst_path");
        if (src.Length > 0 && dst.Length > 0) return Truncate(src + " → " + dst);
        if (src.Length > 0) return Truncate(src);
        foreach (var key in new[] { "query", "pattern", "job_id", "artifact_id" })
        {
            var value = Str(args, key);
            if (value.Length > 0) return Truncate(value);
        }
        return null;
    }

    private static string Str(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim()
            : "";

    private static string Truncate(string s)
    {
        const int max = 240;
        return s.Length <= max ? s : s[..max] + "…";
    }
}
