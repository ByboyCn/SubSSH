// SSH 连接池实现 —— 对应 Go internal/sshpool/pool.go。
// Get/IsOnline/Invalidate/Close 语义与 Go 版一致；TOFU 首次固定、变更拒绝，文案对齐 Go。
// 跳板链：Renci 2024.2 无 Connect(Socket) 公开面，目标连接经跳板上的 SOCKS5 转发端口（ProxyTypes.Socks5）建立。
using System.Collections.Concurrent;
using System.Text;
using Renci.SshNet;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.SshPool;

internal sealed class PoolEntry
{
    public SemaphoreSlim Lock { get; } = new(1, 1);
    public SshRemoteConnection? Connection { get; set; }
    public bool Online { get; set; }
    public CancellationTokenSource? KeepAliveCts { get; set; }
}

internal sealed class SshPool : IConnectionPool, IAsyncDisposable
{
    // maxJumpChain 须与 hostmanager 中的同名常量保持一致。
    internal const int MaxJumpChain = 5;
    private static readonly TimeSpan SshConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly IOneSshStoreFull _store;
    private readonly ICryptoBox _box;
    private readonly ConcurrentDictionary<string, PoolEntry> _entries = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _closed = new();

    public SshPool(IOneSshStoreFull store, ICryptoBox box)
    {
        _store = store;
        _box = box;
    }

    private PoolEntry GetEntry(string name) =>
        _entries.GetOrAdd(name, static _ => new PoolEntry());

    /// <summary>对应 Go Pool.Get。</summary>
    public async Task<IRemoteConnection> GetAsync(string name, CancellationToken ct = default) =>
        await GetCoreAsync(name, [], ct).ConfigureAwait(false);

    /// <summary>对应 Go Pool.get（内部递归，via 记录跳板链）。</summary>
    internal async Task<SshRemoteConnection> GetCoreAsync(string name, List<string> via, CancellationToken ct)
    {
        if (via.Contains(name))
            throw new SubSshException($"跳板链存在循环: {string.Join(" -> ", via.Append(name))}");
        if (via.Count > MaxJumpChain)
            throw new SubSshException($"跳板链超过 {MaxJumpChain} 级");

        var entry = GetEntry(name);
        await entry.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (entry.Connection != null && entry.Online)
                return entry.Connection;

            var h = await _store.GetHostByNameAsync(name, ct).ConfigureAwait(false)
                ?? throw new SubSshException($"unknown host: {name}");

            SshRemoteConnection client;
            try
            {
                client = await DialAsync(h, via, ct).ConfigureAwait(false);
            }
            catch
            {
                entry.Online = false;
                throw;
            }
            entry.Connection = client;
            entry.Online = true;
            StartKeepAlive(name, entry, client);
            return client;
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    /// <summary>对应 Go Pool.dial：认证方式 + TOFU 主机指纹 + 跳板链拨号。</summary>
    private async Task<SshRemoteConnection> DialAsync(HostConfig h, List<string> via, CancellationToken ct)
    {
        if (h.JumpHostId is long jumpId)
        {
            var jump = await _store.GetHostAsync(jumpId, ct).ConfigureAwait(false)
                ?? throw new SubSshException($"查找 {NameAddr(h)} 的跳板主机: unknown host #{jumpId}");
            var jc = await GetCoreAsync(jump.Name, [.. via, h.Name], ct).ConfigureAwait(false);
            // 跳板也是连接池中的普通 entry，拥有独立 keepalive，并可被多个目标复用。
            // 跳板断开后，目标连接会在下次使用或 30 秒内的 keepalive 中失效并重建。
            return await DialOverAsync(jc, h, 0, via, ct).ConfigureAwait(false);
        }

        var holder = new HandshakeHolder();
        var auths = await BuildAuthenticationAsync(h, ct).ConfigureAwait(false);
        var info = new ConnectionInfo(h.Addr, h.Port, h.Username, auths);
        info.Timeout = SshConnectTimeout;
        var client = new SshClient(info);
        AttachTofuCheck(client, h, holder);
        try
        {
            await HandshakeAsync(client, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw HandshakeError(holder, h, ex);
        }
        if (holder.Error is { } tofuErr)
        {
            client.Dispose();
            throw new SubSshException(tofuErr);
        }
        return new SshRemoteConnection(client, h.Name, this, via);
    }

    /// <summary>经已建立的跳板连接拨号并完成目标主机的认证与握手（Go: jc.DialContext + newSSHClient）。</summary>
    internal async Task<SshRemoteConnection> DialOverAsync(
        SshRemoteConnection jump, HostConfig h, int portOverride, List<string> via, CancellationToken ct)
    {
        int port = portOverride > 0 ? portOverride : h.Port;
        var (proxyHost, proxyPort) = await jump.EnsureProxyAsync().ConfigureAwait(false);
        var holder = new HandshakeHolder();
        var auths = await BuildAuthenticationAsync(h, ct).ConfigureAwait(false);
        var info = new ConnectionInfo(h.Addr, port, h.Username, ProxyTypes.Socks5, proxyHost, proxyPort, null, null, auths);
        info.Timeout = SshConnectTimeout;
        var client = new SshClient(info);
        AttachTofuCheck(client, h, holder);
        try
        {
            await HandshakeAsync(client, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw HandshakeError(holder, h, ex, jump: jump.Name);
        }
        if (holder.Error is { } tofuErr)
        {
            client.Dispose();
            throw new SubSshException(tofuErr);
        }
        return new SshRemoteConnection(client, h.Name, this, via);
    }

    /// <summary>对外跳板拨号面（IRemoteConnection.DialAsync）：host 须为库内主机名。</summary>
    internal async Task<SshRemoteConnection> DialThroughAsync(
        SshRemoteConnection jump, string targetName, int port, CancellationToken ct)
    {
        var via = jump.Via.Append(jump.Name).ToList();
        if (via.Contains(targetName))
            throw new SubSshException($"跳板链存在循环: {string.Join(" -> ", via.Append(targetName))}");
        if (via.Count > MaxJumpChain)
            throw new SubSshException($"跳板链超过 {MaxJumpChain} 级");
        var h = await _store.GetHostByNameAsync(targetName, ct).ConfigureAwait(false)
            ?? throw new SubSshException($"unknown host: {targetName}");
        return await DialOverAsync(jump, h, port, via, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // 认证（对应 Go dial 的 auths 段）。
    // -----------------------------------------------------------------------
    private async Task<AuthenticationMethod[]> BuildAuthenticationAsync(HostConfig h, CancellationToken ct)
    {
        switch (h.AuthType)
        {
            case "password":
                if (h.PasswordEnc is not byte[] enc)
                    throw new SubSshException("解密密码失败");
                string plain;
                try
                {
                    plain = Encoding.UTF8.GetString(await _box.OpenAsync(enc, ct).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    throw new SubSshException($"解密密码失败: {ex.Message}");
                }
                return BuildPasswordAuthentication(h.Username, plain);
            case "key":
                if (h.KeyId is not long keyId)
                    throw new SubSshException("主机缺少密钥");
                var k = await _store.GetKeyAsync(keyId, ct).ConfigureAwait(false)
                    ?? throw new SubSshException($"unknown key: {keyId}");
                byte[] pem;
                try
                {
                    pem = await _box.OpenAsync(k.PrivateKeyEnc, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new SubSshException($"解密私钥失败: {ex.Message}");
                }
                return [BuildKeyAuthentication(h.Username, pem)];
            default:
                throw new SubSshException($"不支持的认证类型 \"{h.AuthType}\"");
        }
    }

    /// <summary>对应 Go passwordAuthentication：password + 单轮 keyboard-interactive 回退。</summary>
    private static AuthenticationMethod[] BuildPasswordAuthentication(string username, string password)
    {
        var ki = new KeyboardInteractiveAuthenticationMethod(username);
        var answered = 0;
        ki.AuthenticationPrompt += (sender, e) =>
        {
            if (Interlocked.Increment(ref answered) > 1)
                throw new InvalidOperationException("不支持多轮 keyboard-interactive 认证");
            if (e.Prompts.Count != 1)
                throw new InvalidOperationException("keyboard-interactive 提示数量异常");
            e.Prompts[0].Response = password;
        };
        return [new PasswordAuthenticationMethod(username, password), ki];
    }

    /// <summary>对应 Go 的 ssh.ParsePrivateKey + ssh.PublicKeys。</summary>
    private static PrivateKeyAuthenticationMethod BuildKeyAuthentication(string username, byte[] pem)
    {
        PrivateKeyFile key;
        try
        {
            using var ms = new MemoryStream(pem);
            key = new PrivateKeyFile(ms);
        }
        catch (Exception ex)
        {
            throw new SubSshException($"解析私钥: {ex.Message}");
        }
        return new PrivateKeyAuthenticationMethod(username, key);
    }

    // -----------------------------------------------------------------------
    // TOFU 主机指纹（首次固定、变更拒绝，文案对齐 Go）。
    // HostKeyReceived 是 BaseClient 的同步事件；store 调用经 GetAwaiter 同步等待（控制台宿主无同步上下文）。
    // -----------------------------------------------------------------------
    private void AttachTofuCheck(SshClient client, HostConfig h, HandshakeHolder holder)
    {
        client.HostKeyReceived += (sender, e) =>
        {
            // FingerPrintSHA256 形如 "SHA256:xxx"（base64 无填充），与 Go ssh.FingerprintSHA256 一致。
            var fp = e.FingerPrintSHA256;
            if (string.IsNullOrEmpty(fp))
            {
                e.CanTrust = false;
                holder.Error = "主机指纹计算失败";
                return;
            }
            if (string.IsNullOrEmpty(h.HostKeyFp))
            {
                try
                {
                    _store.UpdateHostFingerprintAsync(h.Id, fp, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    e.CanTrust = false;
                    holder.Error = $"记录主机指纹: {ex.Message}";
                }
            }
            else if (h.HostKeyFp != fp)
            {
                e.CanTrust = false;
                holder.Error = $"SSH 主机指纹变更，需管理员重置（期望 {h.HostKeyFp}，收到 {fp}）";
            }
        };
    }

    // -----------------------------------------------------------------------
    // 建连与握手（对应 Go newSSHClient 的握手期限处理）。
    // -----------------------------------------------------------------------
    private static async Task HandshakeAsync(SshClient client, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SshConnectTimeout);
        await client.ConnectAsync(cts.Token).ConfigureAwait(false);
    }

    private static SubSshException HandshakeError(HandshakeHolder holder, HostConfig h, Exception ex, string? jump = null)
    {
        if (holder.Error is { } tofu)
            return new SubSshException(tofu);
        return jump != null
            ? new SubSshException($"经跳板 {jump} 连接 {NameAddr(h)}: SSH 握手失败: {ex.Message}")
            : new SubSshException($"SSH 握手 {NameAddr(h)}: {ex.Message}");
    }

    private static string NameAddr(HostConfig h) => $"{h.Name}({h.Addr})";

    // -----------------------------------------------------------------------
    // keepalive：30 秒 SendKeepAlive，失败则标记离线并关闭（对应 Go Pool.keepalive）。
    // -----------------------------------------------------------------------
    private void StartKeepAlive(string name, PoolEntry entry, SshRemoteConnection conn)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        entry.KeepAliveCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
                while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
                {
                    try
                    {
#pragma warning disable CS0618 // SendKeepAlive 过时，但需要主动探测失败以摘除离线连接
                        conn.SendKeepAlive();
#pragma warning restore CS0618
                    }
                    catch (Exception ex)
                    {
                        await entry.Lock.WaitAsync(cts.Token).ConfigureAwait(false);
                        try
                        {
                            if (entry.Connection == conn)
                            {
                                entry.Online = false;
                                entry.Connection = null;
                            }
                        }
                        finally
                        {
                            entry.Lock.Release();
                        }
                        conn.Dispose();
                        Console.Error.WriteLine($"[ssh-pool] SSH {name} keepalive 失败: {ex.Message}");
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 池关闭或连接失效，正常退出。
            }
        }, CancellationToken.None);
    }

    /// <summary>连接被外部 Dispose 时从池里摘除（同一连接仍登记时才生效）。</summary>
    internal void MarkOffline(SshRemoteConnection conn)
    {
        var entry = GetEntry(conn.Name);
        entry.Lock.Wait();
        try
        {
            if (entry.Connection == conn)
            {
                entry.Online = false;
                entry.Connection = null;
            }
        }
        finally
        {
            entry.Lock.Release();
        }
        entry.KeepAliveCts?.Cancel();
    }

    /// <summary>对应 Go Pool.IsOnline。</summary>
    public bool IsOnline(string name)
    {
        var entry = GetEntry(name);
        entry.Lock.Wait();
        try
        {
            return entry.Online;
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    /// <summary>对应 Go Pool.Invalidate：关闭并失效，下次 Get 重建。</summary>
    public void Invalidate(string name)
    {
        var entry = GetEntry(name);
        entry.Lock.Wait();
        try
        {
            if (entry.Connection != null)
            {
                entry.Connection.Dispose();
            }
            entry.Connection = null;
            entry.Online = false;
        }
        finally
        {
            entry.Lock.Release();
        }
        entry.KeepAliveCts?.Cancel();
    }

    /// <summary>对应 Go Pool.Close：停掉全部 keepalive 并关闭所有连接（幂等）。</summary>
    public Task CloseAsync(CancellationToken ct = default)
    {
        _closed.Cancel();
        foreach (var entry in _entries.Values)
        {
            entry.Lock.Wait(ct);
            try
            {
                if (entry.Connection != null)
                {
                    entry.Connection.Dispose();
                }
                entry.Connection = null;
                entry.Online = false;
            }
            finally
            {
                entry.Lock.Release();
            }
            entry.KeepAliveCts?.Cancel();
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _closed.Dispose();
    }
}

/// <summary>HostKeyReceived 事件内暂存的 TOFU 结论（事件为同步回调，Connect 结束后统一转异常）。</summary>
internal sealed class HandshakeHolder
{
    public string? Error { get; set; }
}
