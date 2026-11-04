// SubSsh.Plugin.HostManager — 主机管理组件（1:1 移植自 OneSSH internal/hostmanager/manager.go）。
// GPL-3.0，对应 Go hostmanager.Manager：Create/Update/Delete/Test/ResetFingerprint/Archive/Unarchive、
// Input 校验、标签归一化（32 字符/16 个）、跳板链 5 级且不能成环、错误分类（invalid/not_found/conflict/connection/internal）。
using System.Text;
using Microsoft.Data.Sqlite;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.HostManager;

/// <summary>
/// 主机管理器实现（对应 Go hostmanager.Manager）。
/// 依赖由插件按需注入：store 与 crypto 必需（缺任一则插件不发布 IHostManager），
/// connection pool 仅 Test 需要（缺失时 Test 报明确错误，其余操作不受影响）。
/// </summary>
public sealed class HostManager : IHostManager
{
    /// <summary>须与 sshpool 中的同名常量保持一致（Go maxJumpChain）。</summary>
    private const int MaxJumpChain = 5;

    private const int MaxTagLength = 32;
    private const int MaxTagsCount = 16;

    private IOneSshStoreFull? _store;
    private ICryptoBox? _box;
    private IConnectionPool? _pool;

    public void SetStore(IOneSshStoreFull store) => _store = store;
    public void SetCrypto(ICryptoBox box) => _box = box;
    public void SetPool(IConnectionPool? pool) => _pool = pool;

    public async Task<HostConfig> CreateAsync(HostInput input, CancellationToken ct = default)
    {
        var host = await HostFromInputAsync(new HostConfig(), input, updating: false, ct);
        try
        {
            return await RequireStore().CreateHostAsync(host, ct);
        }
        catch (Exception ex)
        {
            throw ClassifyStoreWrite(ex);
        }
    }

    public async Task<HostConfig> UpdateAsync(long id, HostInput input, CancellationToken ct = default)
    {
        var store = RequireStore();
        var old = await GetHostOrNotFoundAsync(id, ct);
        var host = await HostFromInputAsync(old, input, updating: true, ct);
        try
        {
            await store.UpdateHostAsync(host, ct);
        }
        catch (Exception ex)
        {
            throw ClassifyStoreWrite(ex);
        }
        var pool = _pool;
        if (pool != null)
        {
            pool.Invalidate(old.Name);
            if (host.Name != old.Name) pool.Invalidate(host.Name);
        }
        try
        {
            var names = await store.JumpDependentNamesAsync(old.Id, ct);
            if (pool != null)
            {
                foreach (var name in names) pool.Invalidate(name);
            }
        }
        catch
        {
            // 依赖者失效尽力而为（Go 版同样忽略 JumpDependentNames 的 err）
        }
        return host;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        var host = await GetHostOrNotFoundAsync(id, ct);
        _pool?.Invalidate(host.Name);
        try
        {
            var deleted = await RequireStore().DeleteHostAsync(id, ct);
            if (!deleted) throw NotFound();
        }
        catch (SubSshException)
        {
            // store 抛出的业务约束（运行中任务/跳板依赖）→ Conflict（对应 Go ErrHostHasRunningJobs / ErrHostIsJumpHost）
            throw;
        }
        catch (HostManagerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new HostManagerException(ex.Message, HostManagerErrorKind.Internal);
        }
    }

    /// <summary>
    /// 连接测试（对应 Go Manager.Test）：先失效连接再取池内新连接跑 uptime。
    /// 与 Go 版差异：Go 由调用方传入 execx.Runner，此处直接经 IRemoteSession 执行，
    /// 自行实现 15 秒超时与 20 行截断（MaxLines 语义）。
    /// </summary>
    public async Task<ExecResult> TestAsync(long id, CancellationToken ct = default)
    {
        var host = await GetHostOrNotFoundAsync(id, ct);
        var pool = _pool
            ?? throw new SubSshException("连接池组件未就绪，无法测试连接（Test 需要 IConnectionPool，请安装 ssh-pool 插件）");
        pool.Invalidate(host.Name);

        IRemoteConnection client;
        try
        {
            client = await pool.GetAsync(host.Name, ct);
        }
        catch (Exception ex)
        {
            throw new HostManagerException(ex.Message, HostManagerErrorKind.Connection);
        }

        await using (client)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await using var session = await client.OpenSessionAsync(cts.Token);
                await session.StartAsync("uptime", cts.Token);
                var stdout = await ReadAsync(session.Stdout, maxLines: 20, cts.Token);
                var stderr = await ReadAsync(session.Stderr, maxLines: 20, cts.Token);
                var exitCode = await session.WaitAsync(cts.Token);
                var output = stdout.Text + stderr.Text;
                var totalLines = stdout.Lines + stderr.Lines;
                return new ExecResult
                {
                    Stdout = stdout.Text,
                    Stderr = stderr.Text,
                    Output = output,
                    ExitCode = exitCode ?? 0,
                    ExitCodeKnown = exitCode != null,
                    Timeout = false,
                    Truncated = stdout.Truncated || stderr.Truncated,
                    TotalLines = totalLines,
                    TotalBytes = Encoding.UTF8.GetByteCount(output),
                    StdoutBytes = Encoding.UTF8.GetByteCount(stdout.Text),
                    StderrBytes = Encoding.UTF8.GetByteCount(stderr.Text),
                };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HostManagerException("连接测试超时（15 秒）", HostManagerErrorKind.Connection);
            }
            catch (HostManagerException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new HostManagerException(ex.Message, HostManagerErrorKind.Connection);
            }
        }
    }

    public async Task ResetFingerprintAsync(long id, CancellationToken ct = default)
    {
        var host = await GetHostOrNotFoundAsync(id, ct);
        try
        {
            await RequireStore().UpdateHostFingerprintAsync(id, null, ct);
        }
        catch (Exception ex)
        {
            throw new HostManagerException(ex.Message, HostManagerErrorKind.Internal);
        }
        _pool?.Invalidate(host.Name);
    }

    /// <summary>归档主机并断开连接池里的连接；返回归档后的主机（对应 Go Manager.Archive）。</summary>
    public async Task<HostConfig> ArchiveAsync(long id, CancellationToken ct = default)
    {
        var host = await GetHostOrNotFoundAsync(id, ct);
        try
        {
            await RequireStore().ArchiveHostAsync(id, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ct);
        }
        catch (SubSshException)
        {
            // 主机被未归档主机用作跳板 → Conflict（对应 Go ErrHostIsJumpHost）
            throw;
        }
        catch (Exception ex)
        {
            throw new HostManagerException(ex.Message, HostManagerErrorKind.Internal);
        }
        _pool?.Invalidate(host.Name);
        return await GetHostOrNotFoundAsync(id, ct);
    }

    /// <summary>取消归档；连接在下次使用时按需重建（对应 Go Manager.Unarchive）。</summary>
    public async Task<HostConfig> UnarchiveAsync(long id, CancellationToken ct = default)
    {
        try
        {
            await RequireStore().UnarchiveHostAsync(id, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ct);
        }
        catch (SubSshException)
        {
            // 跳板主机仍归档 → Conflict（对应 Go ErrJumpHostArchived）
            throw;
        }
        catch (Exception ex)
        {
            throw new HostManagerException(ex.Message, HostManagerErrorKind.Internal);
        }
        return await GetHostOrNotFoundAsync(id, ct);
    }

    // ------------------------------------------------------------------
    // 输入校验与装配（对应 Go hostFromInput）
    // ------------------------------------------------------------------

    private async Task<HostConfig> HostFromInputAsync(HostConfig old, HostInput input, bool updating, CancellationToken ct)
    {
        var store = RequireStore();
        var box = RequireCrypto();

        var host = old;
        host.Name = input.Name.Trim();
        host.Addr = input.Addr.Trim();
        host.Port = input.Port;
        if (host.Port == 0) host.Port = 22;
        host.Username = input.Username.Trim();
        host.AuthType = input.AuthType;
        if (input.MonitorEnabled != null)
        {
            host.MonitorEnabled = input.MonitorEnabled.Value;
        }
        else if (!updating)
        {
            host.MonitorEnabled = true;
        }

        if (host.Name == "" || host.Addr == "" || host.Username == "")
            throw Invalid("name、addr、username 不能为空");
        if (host.Port < 1 || host.Port > 65535)
            throw Invalid("port 必须在 1 到 65535 之间");

        switch (host.AuthType)
        {
            case "key":
                if (input.KeyId == null)
                    throw Invalid("key 认证必须提供 key_id");
                if (await store.GetKeyAsync(input.KeyId.Value, ct) == null)
                    throw Invalid("key_id 不存在");
                host.KeyId = input.KeyId;
                host.PasswordEnc = null;
                break;

            case "password":
                host.KeyId = null;
                if (input.Password != null)
                {
                    if (input.Password == "")
                        throw Invalid("password 认证必须提供 password");
                    host.PasswordEnc = await box.SealAsync(Encoding.UTF8.GetBytes(input.Password), ct);
                }
                else if (!updating || old.AuthType != "password" || old.PasswordEnc is not { Length: > 0 })
                {
                    throw Invalid("password 认证必须提供 password");
                }
                break;

            default:
                throw Invalid("auth_type 必须是 key 或 password");
        }

        host.JumpHostId = null;
        var jumpName = (input.JumpHost ?? "").Trim();
        if (jumpName != "")
        {
            var jump = await store.GetHostByNameAsync(jumpName, ct)
                ?? throw Invalid("jump_host 不存在: " + jumpName);
            // 与 ArchiveHost/UnarchiveHost 保持同一不变量：未归档主机不能经由已归档的跳板连接
            if (jump.ArchivedAt != null && host.ArchivedAt == null)
                throw Invalid("jump_host 已归档: " + jumpName);

            var seen = new HashSet<long>();
            var cur = jump;
            for (int depth = 1; ; depth++)
            {
                if (updating && cur.Id == old.Id)
                    throw Invalid("跳板链存在循环");
                if (!seen.Add(cur.Id))
                    throw Invalid("跳板链存在循环");
                if (depth > MaxJumpChain)
                    throw Invalid($"跳板链超过 {MaxJumpChain} 级");
                if (cur.JumpHostId == null)
                    break;
                var next = await store.GetHostAsync(cur.JumpHostId.Value, ct)
                    ?? throw new HostManagerException(
                        $"跳板链中断: 主机 #{cur.JumpHostId} 不存在", HostManagerErrorKind.Internal);
                cur = next;
            }
            host.JumpHostId = jump.Id;
        }

        host.Tags = NormalizeTags(input.Tags);
        return host;
    }

    /// <summary>归一化标签：去首尾空白、丢弃空串、按首次出现顺序去重（大小写不敏感），限制长度与数量。</summary>
    private static string[] NormalizeTags(string[]? raw)
    {
        if (raw == null) return [];
        var tags = new List<string>(raw.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tagRaw in raw)
        {
            var tag = tagRaw.Trim();
            if (tag == "" || !seen.Add(tag)) continue;
            if (tag.Length > MaxTagLength)
                throw Invalid($"标签 \"{tag}\" 超过 {MaxTagLength} 字符");
            tags.Add(tag);
        }
        if (tags.Count > MaxTagsCount)
            throw Invalid($"标签最多 {MaxTagsCount} 个");
        return [.. tags];
    }

    // ------------------------------------------------------------------
    // 错误分类（对应 Go invalid/classifyLookup/classifyStoreWrite/KindOf）
    // ------------------------------------------------------------------

    private static HostManagerException Invalid(string message)
        => new(message, HostManagerErrorKind.Invalid);

    private static HostManagerException NotFound()
        => new("主机不存在", HostManagerErrorKind.NotFound);

    private async Task<HostConfig> GetHostOrNotFoundAsync(long id, CancellationToken ct)
        => await RequireStore().GetHostAsync(id, ct) ?? throw NotFound();

    /// <summary>
    /// 唯一/主键约束冲突 → Conflict（对应 Go SQLITE_CONSTRAINT_PRIMARYKEY/UNIQUE 分支）。
    /// hosts.name 唯一约束冲突以中文文案「主机名已存在」呈现（Go 原版直接透出 SQLite 错误文本）。
    /// </summary>
    private static Exception ClassifyStoreWrite(Exception ex)
    {
        if (ex is SqliteException se && se.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT */)
        {
            var extended = se.SqliteExtendedErrorCode;
            bool uniqueOrPk = extended is 1555 /* SQLITE_CONSTRAINT_PRIMARYKEY */ or 2067 /* SQLITE_CONSTRAINT_UNIQUE */;
            if (uniqueOrPk) return new HostManagerException("主机名已存在", HostManagerErrorKind.Conflict);
        }
        if (ex is SubSshException)
        {
            // store 层业务约束 → Conflict（对应 Go store.Err* 家族）
            return new HostManagerException(ex.Message, HostManagerErrorKind.Conflict);
        }
        return new HostManagerException(ex.Message, HostManagerErrorKind.Internal);
    }

    private IOneSshStoreFull RequireStore()
        => _store ?? throw new SubSshException("主机管理不可用：数据库组件未就绪（需要 IOneSshStoreFull）");

    private ICryptoBox RequireCrypto()
        => _box ?? throw new SubSshException("主机管理不可用：密码箱组件未就绪（需要 ICryptoBox）");

    /// <summary>读取输出流并按行截断（对应 Go execx.Options.MaxLines 的语义子集）。</summary>
    private static async Task<(string Text, int Lines, bool Truncated)> ReadAsync(Stream stream, int maxLines, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var sb = new StringBuilder();
        int lines = 0;
        bool truncated = false;
        // 多读一行探测截断（CA2024：异步方法不查 EndOfStream），探测行只用于置位不并入输出
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (lines >= maxLines)
            {
                truncated = true;
                break;
            }
            sb.Append(line).Append('\n');
            lines++;
        }
        return (sb.ToString(), lines, truncated);
    }
}
