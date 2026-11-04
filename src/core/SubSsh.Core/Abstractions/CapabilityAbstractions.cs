using System.Text.Json;
using System.Text.Json.Serialization;
using SubSsh.Core.Models;
using SubSsh.Core.ToolGroups;

namespace SubSsh.Core.Abstractions;

// ============================================================================
// 公共组件抽象层：接口与 OneSSH 各 Go 模块的公开方法一一对应。
// 每个接口都注明其 Go 源文件；由对应插件 Provide（见 CONVENTIONS.md「插件清单与服务依赖」表）。
// ============================================================================

/// <summary>
/// 输出回调（对应 Go execx.Options.OnOutput func(stream string, chunk []byte)）。
/// </summary>
public delegate void OutputCallback(string stream, byte[] chunk);

/// <summary>
/// MCP 工具处理器（对应 mcpserver 的 register[In,Out] handler 签名；
/// 返回结构化结果 + IsError，由 IMcpServer 包装为 JSON-RPC 结果）。
/// </summary>
public delegate Task<McpToolResult> McpToolHandler(JsonElement arguments, McpCallContext context, CancellationToken ct);

/// <summary>
/// MCP 调用上下文（对应 mcpserver FromContext 携带的令牌主体）。
/// </summary>
public sealed record McpCallContext(long TokenId, string TokenName, string[] DisabledTools)
{
    public static readonly McpCallContext Empty = new(0, "", []);
}

/// <summary>
/// MCP 工具结果（对应 mcp.CallToolResult 的文本内容 + isError）。
/// </summary>
public sealed record McpToolResult(IReadOnlyList<string> Texts, bool IsError = false, JsonElement? Structured = null)
{
    public static McpToolResult Text(string text) => new([text]);
    public static McpToolResult Error(string text) => new([text], IsError: true);
}

/// <summary>
/// MCP 工具描述（对应 mcp.Tool：name、description、inputSchema）。
/// </summary>
public sealed record McpToolDescriptor(string Name, string Description, JsonElement InputSchema);

// ---------------------------------------------------------------------------
// 密码箱 —— Go: internal/cryptox/cryptox.go（Box.Seal / Box.Open）
// Provide: Plugin.Crypto；AES-256-GCM，密文格式与原版兼容。
// ---------------------------------------------------------------------------
public interface ICryptoBox
{
    /// <summary>加密（对应 Go Box.Seal：随机 nonce 前置）。</summary>
    Task<byte[]> SealAsync(byte[] plain, CancellationToken ct = default);
    /// <summary>解密（对应 Go Box.Open：nonce 前缀格式）。</summary>
    Task<byte[]> OpenAsync(byte[] blob, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// SSH 连接池 —— Go: internal/sshpool/pool.go（Pool.Get/IsOnline/Invalidate/Close；
// 连接复用、TOFU 指纹固定、跳板链最多 5 级且不能成环、30 秒 keepalive）
// Provide: Plugin.SshPool。实现包装 Renci.SshNet；Core 不引用具体 SSH 实现。
// ---------------------------------------------------------------------------
public interface IConnectionPool
{
    /// <summary>取得（或复用）指定主机的连接（对应 Go Pool.Get）。</summary>
    Task<IRemoteConnection> GetAsync(string name, CancellationToken ct = default);
    /// <summary>连接当前是否在线（对应 Go Pool.IsOnline）。</summary>
    bool IsOnline(string name);
    /// <summary>关闭并失效指定主机的连接，下次 Get 重建（对应 Go Pool.Invalidate）。</summary>
    void Invalidate(string name);
    /// <summary>关闭全部连接（对应 Go Pool.Close）。</summary>
    Task CloseAsync(CancellationToken ct = default);
}

/// <summary>一条已建立的远程连接（对应 Go *ssh.Client 的会话/SFTP 能力面）。</summary>
public interface IRemoteConnection : IAsyncDisposable
{
    /// <summary>打开新的命令会话（对应 Go ssh.Client.NewSession）。</summary>
    Task<IRemoteSession> OpenSessionAsync(CancellationToken ct = default);
    /// <summary>打开 SFTP 通道（对应 Go files.NewClientPool 背后的 sftp.Client）。</summary>
    Task<IRemoteSftp> OpenSftpAsync(CancellationToken ct = default);
    /// <summary>经本连接向 host:port 建立隧道（对应 Go jc.DialContext，跳板链用）。</summary>
    Task<IRemoteConnection> DialAsync(string host, int port, CancellationToken ct = default);
}

/// <summary>一条命令会话（对应 Go *ssh.Session 的流/等待/信号能力面）。</summary>
public interface IRemoteSession : IAsyncDisposable
{
    /// <summary>标准输出流。</summary>
    Stream Stdout { get; }
    /// <summary>标准错误流。</summary>
    Stream Stderr { get; }
    /// <summary>启动远端命令（对应 Go session.Start）。</summary>
    Task StartAsync(string command, CancellationToken ct = default);
    /// <summary>等待命令结束并返回退出码；未知时返回 null（对应 Go session.Wait + ExitError）。</summary>
    Task<int?> WaitAsync(CancellationToken ct = default);
    /// <summary>终止远端进程组（对应 Go terminateSession：先 SSH SIGKILL 再关 channel）。</summary>
    Task KillAsync(CancellationToken ct = default);
}

/// <summary>远端文件元数据（对应 Go os.FileInfo 在 files/sftp 中的使用面）。</summary>
public sealed record RemoteFileStat(string Name, long Size, string Mode, long Mtime, bool Directory);

/// <summary>SFTP 能力面（对应 Go files 客户端封装用到的 sftp.Client 方法）。</summary>
public interface IRemoteSftp : IAsyncDisposable
{
    Task<RemoteFileStat> StatAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<RemoteFileStat>> ReadDirAsync(string path, CancellationToken ct = default);
    Task MkdirAllAsync(string path, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);
    Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default);
    Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default);
    Task RemoveAsync(string path, CancellationToken ct = default);
    Task ChmodAsync(string path, int mode, CancellationToken ct = default);
    /// <summary>读软链接目标；不是软链接时返回 null（对应 Go c.ReadLink）。</summary>
    Task<string?> ReadLinkAsync(string path, CancellationToken ct = default);
    /// <summary>远端当前工作目录（对应 Go files.resolvePath 里的 sftp.Client.Getwd，用于解析 ~）。</summary>
    Task<string> GetwdAsync(CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// 命令执行器 —— Go: internal/execx/runner.go
// （Run：超时/截断/退出码+cwd trailer/artifact；CleanupArtifacts/CleanupCommandOutputs；
//   SHQ/Script：shell 引用与包装脚本；ReadCommandOutput：字节游标分页；
//   ReadArtifact：artifact 日志行分页+grep）
// Provide: Plugin.ExecRunner。
// ---------------------------------------------------------------------------
public sealed record ExecOptions
{
    /// <summary>超时；&lt;=0 时默认 60 秒（对应 Go Options.Timeout）。</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>返回行数上限；&lt;=0 时默认 200（对应 Go Options.MaxLines）。</summary>
    public int MaxLines { get; init; }
    /// <summary>true 返回末尾若干行（对应 Go Options.Tail）。</summary>
    public bool Tail { get; init; }
    /// <summary>非空时完整输出写入 command-runs/&lt;id&gt;.stdout/.stderr.log（对应 Go Options.CaptureID）。</summary>
    public string CaptureId { get; init; } = "";
    /// <summary>输出回调（对应 Go Options.OnOutput）。</summary>
    public OutputCallback? OnOutput { get; init; }
}

/// <summary>命令执行结果（json 字段照抄 Go execx.Result 的 tag）。</summary>
public sealed record ExecResult
{
    [JsonPropertyName("stdout")] public string Stdout { get; init; } = "";
    [JsonPropertyName("stderr")] public string Stderr { get; init; } = "";
    [JsonPropertyName("output")] public string Output { get; init; } = "";
    [JsonPropertyName("exit_code")] public int ExitCode { get; init; }
    [JsonIgnore] public bool ExitCodeKnown { get; init; }
    [JsonPropertyName("cwd")] public string Cwd { get; init; } = "";
    [JsonPropertyName("timeout")] public bool Timeout { get; init; }
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }
    [JsonPropertyName("artifact_id")] public string? ArtifactId { get; init; }
    [JsonPropertyName("total_lines")] public int TotalLines { get; init; }
    [JsonPropertyName("total_bytes")] public long TotalBytes { get; init; }
    [JsonPropertyName("stdout_bytes")] public long StdoutBytes { get; init; }
    [JsonPropertyName("stderr_bytes")] public long StderrBytes { get; init; }
    [JsonPropertyName("output_recorded")] public bool OutputRecorded { get; init; }
    [JsonPropertyName("output_capture_error")] public string? OutputCaptureError { get; init; }
}

/// <summary>输出分页块（json 字段照抄 Go execx.OutputChunk 的 tag；jobs.LogChunk 同形）。</summary>
public sealed record OutputChunk
{
    [JsonPropertyName("content")] public string Content { get; init; } = "";
    [JsonPropertyName("offset_bytes")] public long Offset { get; init; }
    [JsonPropertyName("next_offset_bytes")] public long NextOffset { get; init; }
    [JsonPropertyName("total_bytes")] public long TotalBytes { get; init; }
    [JsonPropertyName("complete")] public bool Complete { get; init; }
}

public interface ICommandRunner
{
    /// <summary>执行远端命令（对应 Go Runner.Run）。</summary>
    Task<ExecResult> RunAsync(
        IRemoteConnection client, string command, string cwd,
        IReadOnlyDictionary<string, string>? env, ExecOptions opt, CancellationToken ct = default);

    /// <summary>清理过期的 artifact 日志（对应 Go Runner.CleanupArtifacts）。</summary>
    Task<int> CleanupArtifactsAsync(DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>按删除白名单清理托管命令输出文件（对应 Go Runner.CleanupCommandOutputs）。</summary>
    Task<int> CleanupCommandOutputsAsync(IReadOnlyCollection<string> deleteIds, CancellationToken ct = default);

    /// <summary>artifact 日志落盘路径（对应 Go Runner.ArtifactPath；id 非 UUID 时抛错）。</summary>
    Task<string> ArtifactPathAsync(string id, CancellationToken ct = default);

    /// <summary>命令完整输出的落盘路径（对应 Go Runner.CommandOutputPath）。</summary>
    Task<string> CommandOutputPathAsync(string id, string stream, CancellationToken ct = default);

    /// <summary>按原始字节偏移分页读取命令输出（对应 Go Runner.ReadCommandOutput）。</summary>
    Task<OutputChunk> ReadCommandOutputAsync(string id, string stream, long offset, int limit, CancellationToken ct = default);

    /// <summary>按行分页并可选 grep 过滤 artifact 日志（对应 Go ReadArtifact）。</summary>
    Task<(string Content, int TotalLines)> ReadArtifactAsync(string path, int offset, int limit, string pattern, CancellationToken ct = default);

    /// <summary>单引号安全转义（对应 Go execx.SHQ）。</summary>
    /// <remarks>static virtual（带缺省体）而非 static abstract：带 static abstract 成员的接口
    /// 不能作为无约束泛型类型实参（CS8920），会导致 Provide&lt;ICommandRunner&gt; 无法编译。</remarks>
    static virtual string SHQ(string s) => throw new NotImplementedException("ICommandRunner.SHQ 由实现插件提供");

    /// <summary>cd + export env + 命令包装脚本（对应 Go execx.Script）。</summary>
    static virtual string Script(string command, string cwd, IReadOnlyDictionary<string, string>? env)
        => throw new NotImplementedException("ICommandRunner.Script 由实现插件提供");
}

// ---------------------------------------------------------------------------
// 文件管理 —— Go: internal/files/manager.go
// （Read/RawRead/Write/Edit/List/Transfer/OpenRead；sha256 乐观锁、临时文件+rename 原子写、
//   二进制拒绝、目录 500 项上限、传输 10 分钟超时 + 落盘校验）
// Provide: Plugin.Files。
// ---------------------------------------------------------------------------
public sealed record FileReadResult
{
    [JsonPropertyName("content")] public string Content { get; init; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("total_lines")] public int TotalLines { get; init; }
}

public sealed record FileWriteResult
{
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("non_atomic")] public bool NonAtomic { get; init; }
    [JsonPropertyName("warning")] public string? Warning { get; init; }
}

public sealed record FileEdit
{
    /// <summary>要被替换的原文，必须在文件中唯一匹配；建议带足上下文以保证唯一。</summary>
    [JsonPropertyName("old_text")] public string OldText { get; init; } = "";
    /// <summary>替换后的新文本，空字符串表示删除这段内容。</summary>
    [JsonPropertyName("new_text")] public string NewText { get; init; } = "";
}

public sealed record FileEditResult
{
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("non_atomic")] public bool NonAtomic { get; init; }
    [JsonPropertyName("warning")] public string? Warning { get; init; }
    [JsonPropertyName("diff")] public string Diff { get; init; } = "";
}

public sealed record FileEntry
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("mode")] public string Mode { get; init; } = "";
    [JsonPropertyName("mtime")] public long Mtime { get; init; }
    [JsonPropertyName("directory")] public bool Directory { get; init; }
    [JsonPropertyName("symlink_target")] public string? SymlinkTarget { get; init; }
}

public sealed record FileTransferResult
{
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("source_sha256")] public string SourceSha256 { get; init; } = "";
    [JsonPropertyName("destination_sha256")] public string? DestinationSha256 { get; init; }
    [JsonPropertyName("verified")] public bool Verified { get; init; }
    [JsonPropertyName("warning")] public string? Warning { get; init; }
}

public interface IFileManager
{
    /// <summary>按行读取远端文本文件（对应 Go files.Manager.Read；二进制拒绝）。</summary>
    Task<FileReadResult> ReadAsync(string host, string file, int offset, int limit, CancellationToken ct = default);

    /// <summary>原始字节读取（对应 Go files.Manager.RawRead）。</summary>
    Task<byte[]> RawReadAsync(string host, string file, long max, CancellationToken ct = default);

    /// <summary>整文件原子写（临时文件 + rename，失败降级非原子）（对应 Go files.Manager.Write）。</summary>
    Task<FileWriteResult> WriteAsync(string host, string file, byte[] data, int mode, CancellationToken ct = default);

    /// <summary>精确替换编辑（old_text 唯一匹配 + sha256 乐观锁 + unified diff）（对应 Go files.Manager.Edit）。</summary>
    Task<FileEditResult> EditAsync(string host, string file, IReadOnlyList<FileEdit> edits, string expectedSha256, CancellationToken ct = default);

    /// <summary>列目录（目录在前、同类按名排序；超 500 项报错）（对应 Go files.Manager.List）。</summary>
    Task<IReadOnlyList<FileEntry>> ListAsync(string host, string dir, CancellationToken ct = default);

    /// <summary>跨主机流式复制 + 目标 sha256 校验（对应 Go files.Manager.Transfer）。</summary>
    Task<FileTransferResult> TransferAsync(string srcHost, string srcPath, string dstHost, string dstPath, CancellationToken ct = default);

    /// <summary>打开受限大小的远端文件流（对应 Go files.Manager.OpenRead，返回流与文件大小）。</summary>
    Task<(Stream Stream, long Size)> OpenReadAsync(string host, string file, long max, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// 图片处理 —— Go: internal/imagex/image.go（Process：解码、比例缩放、
//   不透明 JPEG q80 / 透明 PNG，maxDim 默认 1024、上限 2048）
// Provide: Plugin.Image（ImageSharp 实现）。
// ---------------------------------------------------------------------------
public sealed record ImageProcessResult
{
    public byte[] Data { get; init; } = [];
    public string MimeType { get; init; } = "";
    public int OriginalWidth { get; init; }
    public int OriginalHeight { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

public interface IImageInspector
{
    /// <summary>缩放并重编码图片（对应 Go imagex.Process）。</summary>
    Task<ImageProcessResult> ProcessAsync(byte[] data, int maxDim, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// 主机监控 —— Go: internal/monitor/monitor.go
// （Start/Stop：定时轮询与 7 天清理；Poll：并发上限 5 的全量采样；
//   Sample：/proc + df 解析与差分 CPU；Fresh：双采样立即计算 CPU）
// Provide: Plugin.Monitor。
// ---------------------------------------------------------------------------
public sealed record MonitorDisk
{
    [JsonPropertyName("mount")] public string Mount { get; init; } = "";
    [JsonPropertyName("used_kb")] public long UsedKb { get; init; }
    [JsonPropertyName("total_kb")] public long TotalKb { get; init; }
}

public sealed record MonitorSnapshot
{
    [JsonPropertyName("host_id")] public long HostId { get; init; }
    [JsonPropertyName("ts")] public long Ts { get; init; }
    [JsonPropertyName("cpu_pct")] public double? CpuPct { get; init; }
    [JsonPropertyName("mem_used_kb")] public long? MemUsedKb { get; init; }
    [JsonPropertyName("mem_total_kb")] public long? MemTotalKb { get; init; }
    [JsonPropertyName("load1")] public double? Load1 { get; init; }
    [JsonPropertyName("disks")] public IReadOnlyList<MonitorDisk> Disks { get; init; } = [];
}

public interface IHostMonitor
{
    /// <summary>启动轮询与维护循环（对应 Go monitor.Manager.Start）。</summary>
    Task StartAsync(CancellationToken ct = default);
    /// <summary>停止（对应 Go monitor.Manager.Stop）。</summary>
    Task StopAsync(CancellationToken ct = default);
    /// <summary>立即对所有受监控主机做一轮采样（对应 Go monitor.Manager.Poll，并发去重）。</summary>
    Task PollAsync(CancellationToken ct = default);
    /// <summary>单主机采样（差分 CPU，依赖上一次快照）（对应 Go monitor.Manager.Sample）。</summary>
    Task<MonitorSnapshot> SampleAsync(HostConfig host, CancellationToken ct = default);
    /// <summary>双 /proc 采样立即得到 CPU 百分比（对应 Go monitor.Manager.Fresh）。</summary>
    Task<MonitorSnapshot> FreshAsync(HostConfig host, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// 主机管理 —— Go: internal/hostmanager/manager.go
// （Create/Update/Delete/Test/ResetFingerprint/Archive/Unarchive；
//   Input 校验、标签归一化（32 字符/16 个）、跳板链 5 级且不能成环、错误分类）
// Provide: Plugin.HostManager。
// ---------------------------------------------------------------------------
public sealed record HostInput
{
    /// <summary>主机名，网关内唯一，后续所有工具用它引用这台主机。</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    /// <summary>主机地址或 IP。</summary>
    [JsonPropertyName("addr")] public string Addr { get; init; } = "";
    /// <summary>SSH 端口，省略或 0 表示 22。</summary>
    [JsonPropertyName("port")] public int Port { get; init; }
    /// <summary>SSH 登录用户名。</summary>
    [JsonPropertyName("username")] public string Username { get; init; } = "";
    /// <summary>认证方式：key 或 password。</summary>
    [JsonPropertyName("auth_type")] public string AuthType { get; init; } = "";
    /// <summary>key 认证使用的密钥 ID，auth_type=key 时必填。</summary>
    [JsonPropertyName("key_id")] public long? KeyId { get; init; }
    /// <summary>登录密码；auth_type 为 password 时必填，只写不可读，审计中固定脱敏。</summary>
    [JsonPropertyName("password")] public string? Password { get; init; }
    /// <summary>跳板主机名（可选）：链路最多 5 级且不能成环。</summary>
    [JsonPropertyName("jump_host")] public string? JumpHost { get; init; }
    /// <summary>是否纳入后台资源监控轮询，新建时默认开启。</summary>
    [JsonPropertyName("monitor_enabled")] public bool? MonitorEnabled { get; init; }
    /// <summary>主机标签，自动去空白去重，单个最长 32 字符，最多 16 个。</summary>
    [JsonPropertyName("tags")] public string[]? Tags { get; init; }
}

/// <summary>hostmanager 错误分类（对应 Go hostmanager.ErrorKind）。</summary>
public enum HostManagerErrorKind
{
    Internal = 0,
    Invalid = 1,
    NotFound = 2,
    Conflict = 3,
    Connection = 4,
}

/// <summary>hostmanager 分类异常（对应 Go hostmanager.Error + KindOf）。</summary>
public sealed class HostManagerException(string message, HostManagerErrorKind kind) : Exception(message)
{
    public HostManagerErrorKind Kind { get; } = kind;
}

public interface IHostManager
{
    /// <summary>创建主机（对应 Go hostmanager.Manager.Create）。</summary>
    Task<HostConfig> CreateAsync(HostInput input, CancellationToken ct = default);
    /// <summary>更新主机并失效相关连接（对应 Go hostmanager.Manager.Update）。</summary>
    Task<HostConfig> UpdateAsync(long id, HostInput input, CancellationToken ct = default);
    /// <summary>删除主机（有运行中任务/被当跳板时 Conflict）（对应 Go hostmanager.Manager.Delete）。</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);
    /// <summary>连接测试：先失效连接再跑 uptime（对应 Go hostmanager.Manager.Test）。</summary>
    Task<ExecResult> TestAsync(long id, CancellationToken ct = default);
    /// <summary>重置 TOFU 指纹（对应 Go hostmanager.Manager.ResetFingerprint）。</summary>
    Task ResetFingerprintAsync(long id, CancellationToken ct = default);
    /// <summary>归档主机并断开连接（对应 Go hostmanager.Manager.Archive）。</summary>
    Task<HostConfig> ArchiveAsync(long id, CancellationToken ct = default);
    /// <summary>取消归档（对应 Go hostmanager.Manager.Unarchive）。</summary>
    Task<HostConfig> UnarchiveAsync(long id, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// 后台任务 —— Go: internal/jobs/manager.go
// （Start/StartTracked：setsid+nohup 落盘 PID 与日志；Refresh/RefreshAll：按主机并发探测
//   进程身份并写回状态；Logs/LogChunk：tail/grep 或字节游标；Kill：TERM/KILL 白名单；
//   StartMaintenance：过期任务维护循环）
// Provide: Plugin.Jobs。
// ---------------------------------------------------------------------------
public sealed record JobView
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("host_id")] public long HostId { get; init; }
    [JsonPropertyName("command")] public string Command { get; init; } = "";
    [JsonPropertyName("cwd")] public string Cwd { get; init; } = "";
    [JsonPropertyName("pid")] public long? Pid { get; init; }
    [JsonPropertyName("used_setsid")] public bool UsedSetsid { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("exit_code")] public long? ExitCode { get; init; }
    [JsonPropertyName("started_at")] public long StartedAt { get; init; }
    [JsonPropertyName("finished_at")] public long? FinishedAt { get; init; }
}

public sealed record JobStatus
{
    [JsonPropertyName("job")] public JobView Job { get; init; } = new();
    [JsonPropertyName("log_bytes")] public long LogBytes { get; init; }
    /// <summary>非空表示本次没能登录主机确认状态，Job 是数据库里的上一次快照。</summary>
    [JsonPropertyName("refresh_error")] public string? RefreshError { get; init; }
}

public sealed record JobLogChunk
{
    [JsonPropertyName("content")] public string Content { get; init; } = "";
    [JsonPropertyName("offset_bytes")] public long Offset { get; init; }
    [JsonPropertyName("next_offset_bytes")] public long NextOffset { get; init; }
    [JsonPropertyName("total_bytes")] public long TotalBytes { get; init; }
    [JsonPropertyName("complete")] public bool Complete { get; init; }
}

public interface IJobManager
{
    /// <summary>启动后台任务（对应 Go jobs.Manager.Start）。</summary>
    Task<JobRecord> StartAsync(HostConfig host, long tokenId, string command, string cwd,
        IReadOnlyDictionary<string, string>? env, CancellationToken ct = default);

    /// <summary>启动并关联命令记录 runID（对应 Go jobs.Manager.StartTracked）。</summary>
    Task<JobRecord> StartTrackedAsync(HostConfig host, long tokenId, string command, string cwd,
        IReadOnlyDictionary<string, string>? env, string runId, CancellationToken ct = default);

    /// <summary>刷新单个任务状态（对应 Go jobs.Manager.Refresh）。</summary>
    Task<JobStatus> RefreshAsync(JobRecord job, CancellationToken ct = default);

    /// <summary>批量刷新，结果顺序与输入一一对应（对应 Go jobs.Manager.RefreshAll，整批硬超时）。</summary>
    Task<IReadOnlyList<JobStatus>> RefreshAllAsync(IReadOnlyList<JobRecord> jobs, CancellationToken ct = default);

    /// <summary>读任务日志（tail 行数 / grep -E / 字节偏移续读）（对应 Go jobs.Manager.Logs）。</summary>
    Task<string> LogsAsync(JobRecord job, int tailLines, string pattern, long offset, CancellationToken ct = default);

    /// <summary>字节游标增量读任务日志，单次上限 128 KiB（对应 Go jobs.Manager.LogChunk）。</summary>
    Task<JobLogChunk> LogChunkAsync(JobRecord job, long offset, int limit, CancellationToken ct = default);

    /// <summary>终止任务，signal 仅支持 TERM 或 KILL（对应 Go jobs.Manager.Kill）。</summary>
    Task KillAsync(JobRecord job, string signal, CancellationToken ct = default);

    /// <summary>启动后台维护循环（对应 Go jobs.Manager.StartMaintenance）。</summary>
    Task StartMaintenanceAsync(CancellationToken ct = default);

    /// <summary>停止维护循环（对应 Go jobs.Manager.StopMaintenance）。</summary>
    Task StopMaintenanceAsync(CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// 记忆引擎 —— Go: internal/memoryx/engine.go
// （Remember：去重 + 可选 embedding；Update：内容变化重建向量；
//   Recall：FTS + 向量混合打分（BM25 归一 + 余弦 + 重要度 + recency 半衰期 + veracity 权重）；
//   Sleep：确定性维护去重/衰减/清理）
// Provide: Plugin.Memory。
// ---------------------------------------------------------------------------
public sealed record RememberInput
{
    /// <summary>null 表示写入全局 bank。</summary>
    public long? HostId { get; init; }
    public string Content { get; init; } = "";
    public string Source { get; init; } = "";
    public string Veracity { get; init; } = "";
    public double Importance { get; init; }
    public bool ImportanceSet { get; init; }
    public long? TokenId { get; init; }
}

public sealed record MemoryRememberResult(long Id, bool Deduped, bool Embedded);

public sealed record MemoryRecallResult
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("bank")] public string Bank { get; init; } = "";
    [JsonPropertyName("host_id")] public long? HostId { get; init; }
    [JsonPropertyName("content")] public string Content { get; init; } = "";
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("importance")] public double Importance { get; init; }
    [JsonPropertyName("veracity")] public string Veracity { get; init; } = "";
    [JsonPropertyName("score")] public double Score { get; init; }
    [JsonPropertyName("fts_score")] public double FtsScore { get; init; }
    [JsonPropertyName("dense_score")] public double DenseScore { get; init; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; init; }
    [JsonPropertyName("recall_count")] public long RecallCount { get; init; }
}

public sealed record MemorySleepReport
{
    [JsonPropertyName("deduped")] public long Deduped { get; init; }
    [JsonPropertyName("decayed")] public long Decayed { get; init; }
    [JsonPropertyName("pruned")] public long Pruned { get; init; }
}

public interface IMemoryEngine
{
    /// <summary>写入记忆（同 bank 同内容去重并只升重要度）（对应 Go memoryx.Engine.Remember）。</summary>
    Task<MemoryRememberResult> RememberAsync(RememberInput input, CancellationToken ct = default);

    /// <summary>更新记忆（内容变化时重建 embedding）（对应 Go memoryx.Engine.Update）。</summary>
    Task UpdateAsync(MemoryRecord memory, string? newContent, double? newImportance, string? newVeracity, CancellationToken ct = default);

    /// <summary>召回并返回结果与引擎名（like / fts / hybrid）（对应 Go memoryx.Engine.Recall）。</summary>
    Task<(IReadOnlyList<MemoryRecallResult> Results, string Engine)> RecallAsync(
        long? hostId, bool includeGlobal, string query, int limit, CancellationToken ct = default);

    /// <summary>对单个 bank 做去重/衰减/清理维护（对应 Go memoryx.Engine.Sleep）。</summary>
    Task<MemorySleepReport> SleepAsync(long? hostId, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// 搜索引擎 —— Go: internal/searchx/search.go（Grep：远端 rg --json，exit 127 降级 helper；
//   Find：fd/fdfind，含 --full-path 规则；默认/上限 100/2000、1000/5000；输出字节预算）
// Provide: Plugin.Search。
// ---------------------------------------------------------------------------
public sealed record GrepOptions
{
    /// <summary>搜索模式，默认按正则解释。</summary>
    public string Pattern { get; init; } = "";
    /// <summary>远程搜索起始目录，默认 "."。</summary>
    public string Path { get; init; } = "";
    /// <summary>只搜索匹配该 glob 的文件。</summary>
    public string Glob { get; init; } = "";
    /// <summary>默认 100，上限 2000。</summary>
    public int Limit { get; init; }
    /// <summary>每个匹配前后附带的上下文行数，最大 20。</summary>
    public int Context { get; init; }
    public bool IgnoreCase { get; init; }
    public bool Literal { get; init; }
}

public sealed record GrepLine
{
    public string Path { get; init; } = "";
    public long Line { get; init; }
    public long Column { get; init; }
    public string Text { get; init; } = "";
    public bool Match { get; init; }
}

public sealed record GrepResult
{
    public IReadOnlyList<GrepLine> Lines { get; init; } = [];
    public int MatchCount { get; init; }
    public bool Truncated { get; init; }
    /// <summary>rg / sftp / helper。</summary>
    public string Engine { get; init; } = "";
    /// <summary>降级提示（对应 Go searchcore.GrepResult.Warning，omitempty）。</summary>
    [JsonPropertyName("warning")] public string Warning { get; init; } = "";
}

public sealed record FindOptions
{
    /// <summary>路径 glob；含 "/" 时按完整路径匹配。</summary>
    public string Pattern { get; init; } = "";
    /// <summary>远程搜索起始目录，默认 "."。</summary>
    public string Path { get; init; } = "";
    /// <summary>默认 1000，上限 5000。</summary>
    public int Limit { get; init; }
}

public sealed record FindResult
{
    public IReadOnlyList<string> Paths { get; init; } = [];
    public bool Truncated { get; init; }
    /// <summary>fd / sftp / helper。</summary>
    public string Engine { get; init; } = "";
    /// <summary>降级提示（对应 Go searchcore.FindResult.Warning，omitempty）。</summary>
    [JsonPropertyName("warning")] public string Warning { get; init; } = "";
}

public interface ISearchEngine
{
    /// <summary>远端内容搜索（对应 Go searchx.Manager.Grep）。</summary>
    Task<GrepResult> GrepAsync(string host, GrepOptions opt, CancellationToken ct = default);

    /// <summary>远端文件查找（对应 Go searchx.Manager.Find）。</summary>
    Task<FindResult> FindAsync(string host, FindOptions opt, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// 工具组 —— Go: internal/toolgroups/toolgroups.go（Group/Definition/All、
//   Disabled.Enabled/Names/HidesTool、Parse/ParseList/NormalizeList/Union/GroupNames/AllDisabled）
// 工具名常量在 SubSsh.Core.ToolGroups（ToolGroups.cs）。
// Provide: Plugin.ToolGroups。
// ---------------------------------------------------------------------------
public sealed record ToolGroupDefinition(string Group, IReadOnlyList<string> Tools);

/// <summary>被禁用的分组集合（对应 Go toolgroups.Disabled；null/空表示全部启用）。</summary>
public sealed class DisabledGroups
{
    private readonly HashSet<string> _groups = new(StringComparer.Ordinal);

    public DisabledGroups() { }
    public DisabledGroups(IEnumerable<string> groups)
    {
        foreach (var g in groups) _groups.Add(g);
    }

    public bool IsEmpty => _groups.Count == 0;

    /// <summary>对应 Go Disabled.Enabled。</summary>
    public bool Enabled(string group) => !_groups.Contains(group);

    /// <summary>按 ToolGroupCatalog.All 顺序列出被禁用的分组名（对应 Go Disabled.Names）。</summary>
    public IReadOnlyList<string> Names()
    {
        var names = new List<string>();
        foreach (var def in ToolGroupCatalog.All)
            if (_groups.Contains(def.Group)) names.Add(def.Group);
        return names;
    }

    /// <summary>工具名是否落在被禁用的分组里（对应 Go Disabled.HidesTool）。</summary>
    public bool HidesTool(string name)
    {
        if (_groups.Count == 0) return false;
        foreach (var def in ToolGroupCatalog.All)
        {
            if (!_groups.Contains(def.Group)) continue;
            if (def.Tools.Contains(name)) return true;
        }
        return false;
    }

    /// <summary>合并多份禁用集合（并集）（对应 Go toolgroups.Union）。</summary>
    public static DisabledGroups Union(params IReadOnlyList<DisabledGroups> parts)
    {
        var outSet = new DisabledGroups();
        foreach (var part in parts)
            foreach (var g in part._groups) outSet._groups.Add(g);
        return outSet;
    }
}

public interface IToolGroups
{
    /// <summary>全部分组定义，顺序决定展示次序（对应 Go toolgroups.All）。</summary>
    IReadOnlyList<ToolGroupDefinition> All { get; }

    /// <summary>解析逗号分隔的分组名；未知分组报错（对应 Go toolgroups.Parse）。</summary>
    Task<DisabledGroups> ParseAsync(string spec, CancellationToken ct = default);

    /// <summary>解析分组名列表（令牌/OAuth 同意页的 JSON 数组）（对应 Go toolgroups.ParseList）。</summary>
    Task<DisabledGroups> ParseListAsync(IReadOnlyList<string> groupNames, CancellationToken ct = default);

    /// <summary>校验分组名并按 All 顺序去重（对应 Go toolgroups.NormalizeList）。</summary>
    Task<IReadOnlyList<string>> NormalizeListAsync(IReadOnlyList<string> groupNames, CancellationToken ct = default);

    /// <summary>合并多份禁用集合，取并集（对应 Go toolgroups.Union）。</summary>
    DisabledGroups Union(params IReadOnlyList<DisabledGroups> parts);

    /// <summary>全部分组名（对应 Go toolgroups.GroupNames）。</summary>
    IReadOnlyList<string> GroupNames();

    /// <summary>禁用全部分组，解析失败时的 fail-closed 回退（对应 Go toolgroups.AllDisabled）。</summary>
    DisabledGroups AllDisabled();
}

// ---------------------------------------------------------------------------
// 危险命令/令牌级拦截 —— Go: internal/mcpserver/denylist.go
// （effectiveDisabled：实例级 + 令牌级 denylist 取并集、令牌解析失败 fail-closed；
//   按 tools/call 名称过滤拒绝，resources/read 按 URI 反查工具；
//   auditDeniedTool：写审计 + 发布 tool_call denied 事件）
// Provide: Plugin.Denylist。
// ---------------------------------------------------------------------------
public interface IDenylist
{
    /// <summary>合并实例级与令牌级禁用集合；令牌只可收紧（对应 Go effectiveDisabled）。</summary>
    Task<DisabledGroups> GetEffectiveDisabledAsync(McpCallContext? principal, CancellationToken ct = default);

    /// <summary>拦截通过 MCP 声明的工具调用（对应 installTokenDenylist 中间件的 tools/call 分支）。</summary>
    Task<bool> IsToolAllowedAsync(McpCallContext? principal, string toolName, CancellationToken ct = default);

    /// <summary>记录被拒绝的工具调用审计并发布事件（对应 Go auditDeniedTool）。</summary>
    Task AuditDeniedToolAsync(string toolName, McpCallContext? principal, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// MCP 服务器 —— Go: internal/mcpserver/server.go
// （手写 JSON-RPC 2.0：initialize/tools/list/tools/call/…；token 认证、disabled_tools、
//   command_runs 审计、SSE fanout；serverInfo/instructions 随禁用组收缩）
// Provide: Plugin.McpCore（连同 IToolRegistry）。
// ---------------------------------------------------------------------------
public interface IMcpServer : IAsyncDisposable
{
    /// <summary>initialize 返回的 serverInfo.name（对应 Go serverInfo："OneSSH"）。</summary>
    string Name { get; }

    /// <summary>initialize 返回的 serverInfo.version。</summary>
    string Version { get; }

    /// <summary>处理一条 JSON-RPC 2.0 消息并返回响应 JSON 文本（对应 Go mcp 协议分发面）。</summary>
    Task<string> HandleMessageAsync(string jsonRpcMessage, McpCallContext? principal, CancellationToken ct = default);

    /// <summary>组装当前禁用组下的服务器提示词（对应 Go serverInstructions）。</summary>
    Task<string> GetInstructionsAsync(DisabledGroups disabled, CancellationToken ct = default);

    /// <summary>启动传输无关的服务器（对应 Go Server 的构建与恢复中断命令记录）。</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>停止监控/维护与连接（对应 Go Server.Close）。</summary>
    Task StopAsync(CancellationToken ct = default);
}

/// <summary>
/// 工具注册表：工具包插件（Plugin.Tools.*）经它注册工具（对应 mcpserver 的 register/AddTool 面），
/// 注册需走 ctx.Track 登记，卸载按 LIFO 撤销。
/// </summary>
public interface IToolRegistry
{
    /// <summary>注册一个工具（对应 Go mcp.AddTool + 审计包装）。</summary>
    Task RegisterAsync(McpToolDescriptor tool, McpToolHandler handler, CancellationToken ct = default);

    /// <summary>列出已注册工具；disabled 非空时过滤（对应 tools/list + filterTools）。</summary>
    Task<IReadOnlyList<McpToolDescriptor>> ListAsync(DisabledGroups? disabled, CancellationToken ct = default);

    Task<bool> IsRegisteredAsync(string toolName, CancellationToken ct = default);
}
