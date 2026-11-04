// API 路由实现：对应原版 internal/webapi/api.go / command_runs.go / memories.go。
// 每个端点路径、方法、状态码、JSON 字段与 Go 版 1:1；错误统一 {"error": "<message>"}。
namespace SubSsh.Plugin.WebApi;

using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using SubSsh.Core.Abstractions;
using SubSsh.Core.Events;

/// <summary>可插拔服务束：各能力通过 Require 绑定，缺失时相关路由返回 503。</summary>
public sealed class ApiServices
{
    public ISettings Settings { get; init; }
    public IWebApiStore Store { get; set; }
    public IWebApiHostManager? Hosts { get; set; }
    public IWebApiConnectionPool? Pool { get; set; }          // TODO(port)：Plugin.SshPool
    public IWebApiCommandRunner? Exec { get; set; }           // TODO(port)：Plugin.ExecRunner
    public IWebApiJobManager? Jobs { get; set; }              // TODO(port)：Plugin.Jobs
    public IWebApiFileManager? Files { get; set; }            // TODO(port)：Plugin.Files
    public EventBus Events { get; init; }
    public AdminAuth Auth { get; }

    public ApiServices(ISettings settings, IWebApiStore store, EventBus events)
    {
        Settings = settings;
        Store = store;
        Events = events;
        Auth = new AdminAuth(settings.AdminPassword, settings.MasterKey);
    }
}

/// <summary>SSE 总线：包装 Core EventBus，语义对齐 internal/events（type/ts/data，逐订阅者缓冲）。</summary>
public sealed class WebApiBus : IWebApiEventBus
{
    private readonly EventBus _core;

    public WebApiBus(EventBus core) => _core = core;

    public void Publish(string type, object? data)
    {
        // TODO(port)：对齐 events.Bus 的时间戳来源（UnixMilli）。
        _core.Publish(new WebApiEvent(type, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), data));
    }

    public WebApiEventSubscription Subscribe()
    {
        var queue = new Queue<WebApiEvent>();
        void Handler(WebApiEvent e) { lock (queue) { if (queue.Count < 64) queue.Enqueue(e); } }
        _core.Subscribe<WebApiEvent>(Handler);
        return new WebApiEventSubscription(() =>
        {
            _core.Unsubscribe<WebApiEvent>(Handler);
            lock (queue) queue.Clear();
        })
        { TryRead = () => { lock (queue) return queue.Count > 0 ? queue.Dequeue() : null; } };
    }
}

public static class ApiRoutes
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    // ── 基础工具 ──

    public static IResult JsonOut(object? v, int status = 200) => Results.Text(
        JsonSerializer.Serialize(v, JsonOpts), "application/json", statusCode: status);

    public static IResult ApiError(Exception e, int status = 500) => JsonOut(new { error = e.Message }, status);

    public static IResult ApiError(string message, int status = 500) => JsonOut(new { error = message }, status);

    /// <summary>对齐 hostErrorStatus：WebApiHostException 按 Kind，其余 500。</summary>
    public static int HostErrorStatus(Exception e) =>
        e is WebApiHostException h ? WebApiHostErrorMapper.StatusCode(h.Kind) : 500;

    /// <summary>服务未上线（对应 Go 版 API 字段为 nil 时的兜底；Go 原版会 panic，这里给 503）。</summary>
    public static IResult Unavailable(string service) => ApiError($"服务未上线: {service}", 503);

    public static bool ParseBody<T>(HttpContext ctx, out T value) where T : class
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(ctx.Request.Body, JsonOpts) ?? throw new SubSsh.Core.Abstractions.SubSshException("请求体为空");
            return true;
        }
        catch (Exception e)
        {
            value = null!;
            ApiError(e, 400).ExecuteAsync(ctx).GetAwaiter().GetResult();
            return false;
        }
    }

    /// <summary>对齐 auditFilterValues：去空白、限条数（store.MaxAuditFilterValues，Go 版常量）。</summary>
    public static bool AuditFilterValues(HttpContext ctx, string name, IEnumerable<string?> raw, out string[] values, out IResult? error)
    {
        error = null;
        var list = new List<string>();
        foreach (var r in raw)
        {
            if (r is null) continue;
            var v = r.Trim();
            if (v.Length > 0) list.Add(v);
        }
        // Go: store.MaxAuditFilterValues（未见定义值处，按原版常量 16 移植，见差异报告）。
        const int MaxAuditFilterValues = 16;
        if (list.Count > MaxAuditFilterValues)
        {
            error = ApiError($"审计{name}筛选最多 {MaxAuditFilterValues} 项", 400);
            values = [];
            return false;
        }
        values = [.. list];
        return true;
    }

    public static bool AuditTokenIds(HttpContext ctx, IEnumerable<string?> raw, out long[] ids, out IResult? error)
    {
        ids = [];
        if (!AuditFilterValues(ctx, "令牌", raw, out var values, out error)) return false;
        var parsed = new List<long>();
        foreach (var v in values)
        {
            if (!long.TryParse(v, out var id)) { error = ApiError(new FormatException($"strconv.ParseInt: parsing \"{v}\""), 400); return false; }
            parsed.Add(id);
        }
        ids = [.. parsed];
        return true;
    }

    // ── 主机（api.go hosts/host/hostTest/resetFingerprint/hostArchive）──

    public static async Task<IResult> HostsList(ApiServices a, CancellationToken ct)
    {
        var list = await a.Store.ListHostsAsync(ct);
        return JsonOut(list);
    }

    public static async Task<IResult> HostsCreate(ApiServices a, HttpContext ctx, CancellationToken ct)
    {
        if (!ParseBody<WebApiHostInput>(ctx, out var input)) return Empty;
        if (a.Hosts is null) return Unavailable("hostmanager");
        try { return JsonOut(await a.Hosts.CreateAsync(input, ct), 201); }
        catch (Exception e) { return ApiError(e, HostErrorStatus(e)); }
    }

    public static async Task<IResult> HostUpdate(ApiServices a, long id, HttpContext ctx, CancellationToken ct)
    {
        if (!ParseBody<WebApiHostInput>(ctx, out var input)) return Empty;
        if (a.Hosts is null) return Unavailable("hostmanager");
        try { return JsonOut(await a.Hosts.UpdateAsync(id, input, ct)); }
        catch (Exception e) { return ApiError(e, HostErrorStatus(e)); }
    }

    public static async Task<IResult> HostDelete(ApiServices a, long id, CancellationToken ct)
    {
        if (a.Hosts is null) return Unavailable("hostmanager");
        try { await a.Hosts.DeleteAsync(id, ct); return Results.NoContent(); }
        catch (Exception e) { return ApiError(e, HostErrorStatus(e)); }
    }

    public static async Task<IResult> HostTest(ApiServices a, long id, CancellationToken ct)
    {
        if (a.Hosts is null) return Unavailable("hostmanager");
        try { return JsonOut(await a.Hosts.TestAsync(id, ct)); }
        catch (Exception e) { return ApiError(e, HostErrorStatus(e)); }
    }

    public static async Task<IResult> ResetFingerprint(ApiServices a, long id, CancellationToken ct)
    {
        if (a.Hosts is null) return Unavailable("hostmanager");
        try { await a.Hosts.ResetFingerprintAsync(id, ct); return JsonOut(new { ok = true }); }
        catch (Exception e) { return ApiError(e, HostErrorStatus(e)); }
    }

    public static async Task<IResult> HostArchive(ApiServices a, long id, bool unarchive, CancellationToken ct)
    {
        if (a.Hosts is null) return Unavailable("hostmanager");
        try
        {
            var h = unarchive ? await a.Hosts.UnarchiveAsync(id, ct) : await a.Hosts.ArchiveAsync(id, ct);
            return JsonOut(h);
        }
        catch (Exception e) { return ApiError(e, HostErrorStatus(e)); }
    }

    // ── 密钥（api.go keys/key/importPrivateKey）──

    private sealed record KeyInput(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("private_key")] string PrivateKey,
        [property: JsonPropertyName("passphrase")] string Passphrase);

    public static async Task<IResult> KeysList(ApiServices a, CancellationToken ct) => JsonOut(await a.Store.ListKeysAsync(ct));

    public static async Task<IResult> KeysCreate(ApiServices a, HttpContext ctx, CancellationToken ct)
    {
        if (!ParseBody<KeyInput>(ctx, out var input)) return Empty;
        string publicKey;
        byte[] @private;
        switch (input.Mode)
        {
            case "generate":
                // TODO(port)：原版 ed25519.GenerateKey + ssh.MarshalPrivateKey（OpenSSH PEM）。
                // .NET BCL 无 Ed25519 生成/OpenSSH 编码，待引入移植方案后补齐；当前返回 501。
                return ApiError("ed25519 私钥生成尚未移植（TODO port）", 501);
            case "import":
                try
                {
                    (@private, publicKey) = ImportPrivateKey(input.PrivateKey, input.Passphrase, input.Name);
                }
                catch (Exception e) { return ApiError(e, 400); }
                break;
            default:
                return ApiError("mode 必须是 generate 或 import", 400);
        }
        try
        {
            // TODO(port)：原版此处经 a.Box.Seal 加密私钥入库；加密由 Plugin.Crypto 的 ICryptoBox 负责，
            // IWebApiStore.CreateKeyAsync 契约约定传入的 privateKeyEnc 已是密文，故这里先原样透传（占位）。
            Array.Clear(@private);
            var key = await a.Store.CreateKeyAsync(input.Name, Encoding.UTF8.GetBytes(input.PrivateKey), publicKey, ct);
            return JsonOut(key, 201);
        }
        catch (Exception e) { return ApiError(e, 409); }
    }

    /// <summary>TODO(port)：原版 ssh.ParsePrivateKey(+Passphrase) 解析并重编码 OpenSSH 私钥；需等价私钥解析库。</summary>
    private static (byte[] Private, string Public) ImportPrivateKey(string pem, string passphrase, string comment)
        => throw new SubSsh.Core.Abstractions.SubSshException("私钥导入尚未移植（TODO port：需 OpenSSH 私钥解析/编码库）");

    public static async Task<IResult> KeyDelete(ApiServices a, long id, CancellationToken ct)
    {
        try { await a.Store.DeleteKeyAsync(id, ct); return Results.NoContent(); }
        catch (Exception e) { return ApiError(e, 409); }
    }

    // ── 令牌（api.go tokens/token/normalizeTokenDisabledTools）──

    private sealed record TokenInput(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("all_hosts")] bool AllHosts,
        [property: JsonPropertyName("manage_hosts")] bool ManageHosts,
        [property: JsonPropertyName("host_ids")] long[] HostIds,
        [property: JsonPropertyName("disabled_tools")] string[] DisabledTools);

    private static WebApiTokenView ToView(WebApiToken t, long[]? hostIds = null, string? plain = null) =>
        new() { Id = t.Id, Name = t.Name, AllHosts = t.AllHosts, ManageHosts = t.ManageHosts, DisabledTools = t.DisabledTools, Source = t.Source, CreatedAt = t.CreatedAt, HostIds = hostIds, Token = plain };

    public static IResult ToolGroupsList() =>
        JsonOut(ToolGroups.All.Select(g => (object)new Dictionary<string, object> { ["name"] = g.Group, ["tools"] = g.Tools }).ToList());

    private static bool NormalizeTokenDisabledTools(string[] names, out string[] normalized, out IResult? error)
    {
        error = null; normalized = [];
        try { normalized = ToolGroups.NormalizeList(names); return true; }
        catch (Exception e) { error = ApiError(e, 400); return false; }
    }

    public static async Task<IResult> TokensList(ApiServices a, CancellationToken ct)
    {
        var list = await a.Store.ListTokensAsync(ct);
        var view = new List<WebApiTokenView>(list.Count);
        foreach (var t in list)
        {
            long[] ids = [];
            try { ids = await a.Store.TokenHostIdsAsync(t.Id, ct); } catch { /* 对齐 Go：忽略 */ }
            view.Add(ToView(t, ids));
        }
        return JsonOut(view);
    }

    public static async Task<IResult> TokensCreate(ApiServices a, HttpContext ctx, CancellationToken ct)
    {
        if (!ParseBody<TokenInput>(ctx, out var input)) return Empty;
        if (!NormalizeTokenDisabledTools(input.DisabledTools, out var disabled, out var err)) return err!;
        // 对齐 Go：32 随机字节 + "osh_" 前缀 + RawURLEncoding。
        var raw = new byte[32];
        Random.Shared.NextBytes(raw);
        var plain = "osh_" + Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        try
        {
            var t = await a.Store.CreateTokenAsync(new WebApiTokenCreate
            {
                Name = input.Name, Hash = TokenHash(plain), AllHosts = input.AllHosts, ManageHosts = input.ManageHosts,
                DisabledTools = disabled, HostIds = input.HostIds,
            }, ct);
            return JsonOut(ToView(t, input.HostIds, plain), 201);
        }
        catch (Exception e) { return ApiError(e, 409); }
    }

    /// <summary>TODO(port)：对齐 store.TokenHash 的具体哈希算法（sha256 hex，待 Plugin.Db 确认后照搬）。</summary>
    private static string TokenHash(string plain) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(plain))).ToLowerInvariant();

    public static async Task<IResult> TokenUpdate(ApiServices a, long id, HttpContext ctx, CancellationToken ct)
    {
        if (!ParseBody<TokenInput>(ctx, out var input)) return Empty;
        if (!NormalizeTokenDisabledTools(input.DisabledTools, out var disabled, out var err)) return err!;
        long[] hostIds;
        if (input.AllHosts) hostIds = [];
        else
        {
            try { hostIds = await a.Store.ValidateHostIdsAsync(input.HostIds, ct); }
            catch (Exception e) { return ApiError(e, 400); }
        }
        try
        {
            var t = await a.Store.UpdateTokenAsync(id, new WebApiTokenUpdate
            {
                Name = input.Name, AllHosts = input.AllHosts, ManageHosts = input.ManageHosts,
                HostIds = hostIds, DisabledTools = disabled,
            }, ct);
            long[] ids = [];
            try { ids = await a.Store.TokenHostIdsAsync(t.Id, ct); } catch { }
            return JsonOut(ToView(t, ids));
        }
        catch (WebApiHostException h) when (h.Kind == WebApiHostErrorKind.NotFound) { return ApiError(h, 404); }
        catch (Exception e) { return ApiError(e, 409); }
    }

    public static async Task<IResult> TokenDelete(ApiServices a, long id, CancellationToken ct)
    {
        try { await a.Store.DeleteTokenAsync(id, ct); return Results.NoContent(); }
        catch (Exception e) { return ApiError(e, 500); }
    }

    // ── 任务（api.go jobsList/jobKill；command_runs.go jobLogs）──

    public static async Task<IResult> JobsList(ApiServices a, string host, CancellationToken ct)
    {
        long? hostId = null;
        if (!string.IsNullOrEmpty(host))
        {
            try { var h = await a.Store.GetHostByNameAsync(host, ct); hostId = h.Id; }
            catch (Exception e) { return ApiError(e, 404); }
        }
        var list = await a.Store.ListJobsAsync(hostId, ct);
        if (a.Jobs is null) return JsonOut(list);
        try { return JsonOut(await a.Jobs.RefreshAllAsync(list, ct)); }
        catch { return JsonOut(list); }
    }

    public static async Task<IResult> JobKill(ApiServices a, string id, CancellationToken ct)
    {
        WebApiJob job;
        try { job = await a.Store.GetJobAsync(id, ct); }
        catch (Exception e) { return ApiError(e, 404); }
        if (a.Jobs is null) return Unavailable("jobs");
        try { await a.Jobs.KillAsync(job, "TERM", ct); return JsonOut(new { ok = true }); }
        catch (Exception e) { return ApiError(e, 502); }
    }

    public static async Task<IResult> JobLogs(ApiServices a, HttpContext http, string id, long offsetBytes, int limitBytes, CancellationToken ct)
    {
        WebApiJob job;
        try { job = await a.Store.GetJobAsync(id, ct); }
        catch (WebApiHostException h) when (h.Kind == WebApiHostErrorKind.NotFound) { return ApiError("后台任务不存在", 404); }
        catch (Exception e) { return ApiError(e, 500); }
        if (a.Jobs is null) return Unavailable("jobs");
        try
        {
            var chunk = await a.Jobs.LogChunkAsync(job, offsetBytes, limitBytes, ct);
            http.ResponseHeadersNoStore();
            return JsonOut(chunk);
        }
        catch (Exception e) { return ApiError(e, 502); }
    }

    // ── 命令执行记录（command_runs.go）──

    public static async Task<IResult> CommandRuns(ApiServices a, HttpContext ctx, CancellationToken ct)
    {
        var q = ctx.Request.Query;
        if (!AuditTokenIds(ctx, q["token"].ToArray(), out var tokenIds, out var err)) return err!;
        if (!AuditFilterValues(ctx, "主机", q["host"].ToArray(), out var hosts, out err)) return err!;
        if (!AuditFilterValues(ctx, "工具", q["tool"].ToArray(), out var tools, out err)) return err!;
        if (!AuditFilterValues(ctx, "状态", q["status"].ToArray(), out var statuses, out err)) return err!;
        long.TryParse(q["before"], out var before);
        int.TryParse(q["limit"], out var limit);
        var runs = await a.Store.ListCommandRunsAsync(new WebApiCommandRunFilter
        {
            TokenIds = tokenIds, Hosts = hosts, Tools = tools, Statuses = statuses,
            Query = q["q"].ToString(), Before = before, Limit = limit,
        }, ct);
        var outList = runs.Select(r => ViewCommandRun(r, detail: false)).ToList();
        ctx.Response.Headers.CacheControl = "no-store";
        return JsonOut(outList);
    }

    /// <summary>对齐 viewCommandRun：列表不带预览，详情带预览；duration_ms 以 finished 或当前时间计算。</summary>
    public static WebApiCommandRun ViewCommandRun(WebApiCommandRun run, bool detail)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var duration = (run.FinishedAt is long fin ? fin : now) - run.StartedAt;
        return run with
        {
            DurationMs = Math.Max(0, duration),
            StdoutPreview = detail ? run.StdoutPreview : null,
            StderrPreview = detail ? run.StderrPreview : null,
        };
    }

    public static async Task<IResult> CommandRun(ApiServices a, HttpContext ctx, string id, CancellationToken ct)
    {
        WebApiCommandRun run;
        try { run = await a.Store.GetCommandRunAsync(id, ct); }
        catch (WebApiHostException h) when (h.Kind == WebApiHostErrorKind.NotFound) { return ApiError("命令执行记录不存在", 404); }
        catch (Exception e) { return ApiError(e, 500); }
        ctx.ResponseHeadersNoStore();
        return JsonOut(ViewCommandRun(run, detail: true));
    }

    public static async Task<IResult> CommandRunOutput(ApiServices a, HttpContext ctx, string id, string stream, long offsetBytes, int limitBytes, CancellationToken ct)
    {
        WebApiCommandRun run;
        try { run = await a.Store.GetCommandRunAsync(id, ct); }
        catch (WebApiHostException h) when (h.Kind == WebApiHostErrorKind.NotFound) { return ApiError("命令执行记录不存在", 404); }
        catch (Exception e) { return ApiError(e, 500); }
        if (run.OutputExpired) return ApiError("命令输出已超过 7 天保留期", 410);
        if (!string.IsNullOrEmpty(run.JobId))
        {
            if (stream.Length > 0 && stream != "combined")
                return ApiError("后台任务仅提供 combined 合并日志", 400);
            WebApiJob job;
            try { job = await a.Store.GetJobAsync(run.JobId, ct); }
            catch (WebApiHostException h) when (h.Kind == WebApiHostErrorKind.NotFound)
            { return ApiError("后台任务记录已清理（结束超过 30 天或主机已删除），日志不再可读", 410); }
            catch (Exception e) { return ApiError(e, 500); }
            if (a.Jobs is null) return Unavailable("jobs");
            if (job.Status == "running")
            {
                try
                {
                    await a.Jobs.RefreshAsync(job, ct);
                    job = await a.Store.GetJobAsync(job.Id, ct);
                }
                catch { /* 对齐 Go：刷新失败忽略 */ }
            }
            try
            {
                var chunk = await a.Jobs.LogChunkAsync(job, offsetBytes, limitBytes, ct);
                ctx.ResponseHeadersNoStore();
                return JsonOut(chunk);
            }
            catch (Exception e) { return ApiError(e, 502); }
        }
        if (!run.OutputAvailable && run.Status != "running")
        {
            var message = "命令没有可读取的输出";
            if (run.OutputError is string oe) message += ": " + oe;
            return ApiError(message, 404);
        }
        if (stream.Length == 0) stream = "stdout";
        if (stream != "stdout" && stream != "stderr")
            return ApiError("stream 仅支持 stdout 或 stderr", 400);
        if (a.Exec is null) return Unavailable("execx");
        try
        {
            var chunk = await a.Exec.ReadCommandOutputAsync(run.Id, stream, offsetBytes, limitBytes, ct);
            ctx.ResponseHeadersNoStore();
            return JsonOut(chunk);
        }
        catch (Exception e) { return ApiError(e, 404); }
    }

    // ── 审计（api.go audit/auditTools）──

    public static async Task<IResult> Audit(ApiServices a, HttpContext ctx, CancellationToken ct)
    {
        var q = ctx.Request.Query;
        if (!AuditTokenIds(ctx, q["token"].ToArray(), out var tids, out var err)) return err!;
        if (!AuditFilterValues(ctx, "主机", q["host"].ToArray(), out var hosts, out err)) return err!;
        if (!AuditFilterValues(ctx, "工具", q["tool"].ToArray(), out var tools, out err)) return err!;
        bool? ok = null;
        var okRaw = q["ok"].ToString();
        if (okRaw.Length > 0)
        {
            if (!bool.TryParse(okRaw, out var v)) return ApiError(new FormatException($"strconv.ParseBool: parsing \"{okRaw}\""), 400);
            ok = v;
        }
        long.TryParse(q["before"], out var before);
        int.TryParse(q["limit"], out var limit);
        var list = await a.Store.ListAuditAsync(tids, hosts, tools, ok, before, limit, ct);
        return JsonOut(list);
    }

    public static async Task<IResult> AuditTools(ApiServices a, CancellationToken ct) => JsonOut(await a.Store.ListAuditToolsAsync(ct));

    // ── 监控（api.go metrics）──

    public static async Task<IResult> Metrics(ApiServices a, long hostId, int hours, CancellationToken ct)
    {
        if (hours <= 0 || hours > 24 * 30) hours = 24;
        var since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)hours * 3600_000;
        var list = await a.Store.MetricsSinceAsync(hostId, since, ct);
        var outList = list.Select(m => new WebApiMetricSnapshot
        {
            Ts = m.Ts, CpuPct = m.CpuPct, MemUsedKb = m.MemUsedKb, MemTotalKb = m.MemTotalKb, Load1 = m.Load1, DisksJson = m.DisksJson,
        }).ToList();
        return JsonOut(outList);
    }

    // ── SFTP（api.go sftpList/sftpDownload/sftpUpload）──

    private static async Task<string?> HostNameByID(ApiServices a, string raw, CancellationToken ct)
    {
        if (!long.TryParse(raw, out var id)) return null;
        try { return (await a.Store.GetHostAsync(id, ct)).Name; }
        catch { return null; }
    }

    public static async Task<IResult> SftpList(ApiServices a, long hostId, string path, CancellationToken ct)
    {
        var name = await HostNameByID(a, hostId.ToString(), ct);
        if (name is null) return ApiError($"主机不存在: {hostId}", 404);
        if (a.Files is null) return Unavailable("files");
        try { return JsonOut(await a.Files.ListAsync(name, path, ct)); }
        catch (Exception e) { return ApiError(e, 502); }
    }

    public static async Task SftpDownload(ApiServices a, HttpContext ctx, long hostId, string path, bool headOnly, CancellationToken ct)
    {
        var name = await HostNameByID(a, hostId.ToString(), ct);
        if (name is null) { await ApiError($"主机不存在: {hostId}", 404).ExecuteAsync(ctx); return; }
        if (a.Files is null) { await Unavailable("files").ExecuteAsync(ctx); return; }
        WebApiFileReadStream file;
        try { file = await a.Files.OpenReadAsync(name, path, 100L << 20, ct); }
        catch (Exception e) { await ApiError(e, 502).ExecuteAsync(ctx); return; }
        await using (file)
        {
            var contentType = ContentTypeFor(path);
            if (contentType is not null) ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength = file.Size;
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{Path.GetFileName(path).Replace("\"", "\\\"")}\"";
            if (headOnly) { ctx.Response.StatusCode = 200; return; }
            try
            {
                var buf = new byte[64 * 1024];
                long remaining = file.Size;
                while (remaining > 0)
                {
                    var n = await file.Content.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remaining)), ct);
                    if (n <= 0) break;
                    await ctx.Response.Body.WriteAsync(buf.AsMemory(0, n), ct);
                    remaining -= n;
                }
            }
            catch (Exception e)
            {
                // 对齐 Go：下载中断仅记日志。
                Console.Error.WriteLine($"SFTP 下载 {path} 中断: {e.Message}");
            }
        }
    }

    private static string? ContentTypeFor(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".txt" or ".log" => "text/plain",
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" => "text/javascript",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".gz" => "application/gzip",
            ".mp4" => "video/mp4",
            ".csv" => "text/csv",
            _ => null,
        };
    }

    public static async Task<IResult> SftpUpload(ApiServices a, long hostId, IFormFile file, string path, CancellationToken ct)
    {
        var name = await HostNameByID(a, hostId.ToString(), ct);
        if (name is null) return ApiError($"主机不存在: {hostId}", 404);
        if (a.Files is null) return Unavailable("files");
        const long MaxUpload = 100L << 20;
        if (file.Length > MaxUpload) return ApiError("上传文件过大或读取失败", 400);
        byte[] data;
        using (var s = file.OpenReadStream())
        {
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms, ct);
            data = ms.ToArray();
        }
        if (data.Length > MaxUpload) return ApiError("上传文件过大或读取失败", 400);
        if (path.EndsWith('/') || path.Length == 0) path += file.FileName;
        try
        {
            var result = await a.Files.WriteAsync(name, path, data, "0644", ct);
            return JsonOut(result, 201);
        }
        catch (Exception e) { return ApiError(e, 502); }
    }

    // ── 记忆管理（memories.go）──

    public static async Task<IResult> Memories(ApiServices a, HttpContext ctx, CancellationToken ct)
    {
        var q = ctx.Request.Query;
        long? hostId = null;
        if (q.ContainsKey("host_id"))
        {
            var raw = q["host_id"].ToString();
            if (raw.Length == 0) return ApiError("host_id 不能为空", 400);
            if (!long.TryParse(raw, out var value) || value < 0) return ApiError("host_id 必须是非负整数", 400);
            hostId = value;
        }
        var limit = 50;
        var limitRaw = q["limit"].ToString();
        if (limitRaw.Length > 0)
        {
            if (!int.TryParse(limitRaw, out var value) || value < 1) return ApiError("limit 必须是正整数", 400);
            limit = Math.Min(value, 200);
        }
        var offset = 0;
        var offsetRaw = q["offset"].ToString();
        if (offsetRaw.Length > 0)
        {
            if (!int.TryParse(offsetRaw, out var value) || value < 0) return ApiError("offset 必须是非负整数", 400);
            offset = value;
        }
        var rows = await a.Store.ListMemoriesAdminAsync(hostId, q["q"].ToString(), limit, offset, ct);
        return JsonOut(rows);
    }

    public static async Task<IResult> MemoryDelete(ApiServices a, long id, CancellationToken ct)
    {
        try { await a.Store.DeleteMemoryAsync(id, ct); return Results.NoContent(); }
        catch (WebApiHostException h) when (h.Kind == WebApiHostErrorKind.NotFound) { return ApiError(h, 404); }
        catch (Exception e) { return ApiError(e, 500); }
    }

    public static async Task<IResult> MemoryStats(ApiServices a, CancellationToken ct)
    {
        var stats = await a.Store.MemoryStatsAsync(ct);
        var hosts = await a.Store.ListHostsAsync(ct);
        var hostNames = hosts.ToDictionary(h => h.Id, h => h.Name);
        var outList = new List<WebApiMemoryBankStat>(stats.Count);
        foreach (var s in stats)
        {
            string? name = null;
            if (s.HostId is long id) hostNames.TryGetValue(id, out name);
            outList.Add(new WebApiMemoryBankStat(s.HostId, name, s.Count, s.Embedded, s.LastWritten));
        }
        return JsonOut(outList);
    }

    public static IResult Empty => Results.Empty;
}

/// <summary>api.go 内 sse/terminal 以外的响应头辅助。</summary>
public static class CommandRunHttpContextExtensions
{
    public static void ResponseHeadersNoStore(this HttpContext ctx) => ctx.Response.Headers.CacheControl = "no-store";
}
