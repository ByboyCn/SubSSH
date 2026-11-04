using System.Text.Json.Serialization;

namespace SubSsh.Core.Models;

// 数据模型对齐 OneSSH internal/store/models.go 的 json tag（camelCase 属性）。
// sql.NullInt64/NullString/NullFloat64 → C# 可空原生类型；json:"-" → [JsonIgnore]。

/// <summary>SSH 密钥（对应 Go store.Key）。</summary>
public sealed class SshKey
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>私钥密文，对外不序列化（Go json:"-"）。</summary>
    [JsonIgnore] public byte[]? PrivateKeyEnc { get; set; }
    [JsonPropertyName("public_key")] public string PublicKey { get; set; } = "";
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
}

/// <summary>主机配置（对应 Go store.Host）。</summary>
public sealed class HostConfig
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("addr")] public string Addr { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("auth_type")] public string AuthType { get; set; } = "";
    [JsonIgnore] public long? KeyId { get; set; }
    [JsonIgnore] public byte[]? PasswordEnc { get; set; }
    [JsonIgnore] public string? HostKeyFp { get; set; }
    [JsonIgnore] public long? JumpHostId { get; set; }
    [JsonPropertyName("monitor_enabled")] public bool MonitorEnabled { get; set; }
    [JsonPropertyName("tags")] public string[] Tags { get; set; } = [];
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    /// <summary>非空表示已归档：保留配置与历史，但不监控、不对 MCP 令牌可见（Go json:"-"）。</summary>
    [JsonIgnore] public long? ArchivedAt { get; set; }
}

/// <summary>主机的对外视图（对应 Go store.HostView / Host.View()）。</summary>
public sealed class HostView
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("addr")] public string Addr { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("auth_type")] public string AuthType { get; set; } = "";
    [JsonPropertyName("key_id")] public long? KeyId { get; set; }
    [JsonPropertyName("hostkey_fp")] public string? HostKeyFp { get; set; }
    [JsonPropertyName("jump_host_id")] public long? JumpHostId { get; set; }
    [JsonPropertyName("monitor_enabled")] public bool MonitorEnabled { get; set; }
    [JsonPropertyName("tags")] public string[] Tags { get; set; } = [];
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("archived")] public bool Archived { get; set; }
    [JsonPropertyName("archived_at")] public long? ArchivedAt { get; set; }

    /// <summary>对应 Go Host.View()。</summary>
    public static HostView From(HostConfig h)
    {
        var v = new HostView
        {
            Id = h.Id, Name = h.Name, Addr = h.Addr, Port = h.Port, Username = h.Username,
            AuthType = h.AuthType, MonitorEnabled = h.MonitorEnabled,
            Tags = h.Tags.Length == 0 ? [] : h.Tags, CreatedAt = h.CreatedAt,
        };
        if (h.KeyId is long keyId) v.KeyId = keyId;
        if (h.JumpHostId is long jumpId) v.JumpHostId = jumpId;
        if (h.HostKeyFp is string fp) v.HostKeyFp = fp;
        if (h.ArchivedAt is long archivedAt) { v.Archived = true; v.ArchivedAt = archivedAt; }
        return v;
    }
}

/// <summary>MCP 访问令牌（对应 Go store.Token）。</summary>
public sealed class ApiToken
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("all_hosts")] public bool AllHosts { get; set; }
    [JsonPropertyName("manage_hosts")] public bool ManageHosts { get; set; }
    [JsonPropertyName("disabled_tools")] public string[] DisabledTools { get; set; } = [];
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonIgnore] public long? LastUsedAt { get; set; }
    [JsonIgnore] public long? ExpiresAt { get; set; }
    [JsonIgnore] public string? Resource { get; set; }
    [JsonIgnore] public string? ClientId { get; set; }
}

/// <summary>exec 的持久化会话（cwd + env；对应 Go store.Session，Go 无 json tag）。</summary>
public sealed class McpSession
{
    public long Id { get; set; }
    public long TokenId { get; set; }
    public long HostId { get; set; }
    public string Label { get; set; } = "";
    public string Cwd { get; set; } = "";
    public Dictionary<string, string> Env { get; set; } = [];
    public long UpdatedAt { get; set; }
}

/// <summary>后台任务记录（对应 Go store.Job）。</summary>
public sealed class JobRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("host_id")] public long HostId { get; set; }
    [JsonIgnore] public long? TokenId { get; set; }
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("cwd")] public string Cwd { get; set; } = "";
    [JsonPropertyName("pid")] public long? Pid { get; set; }
    [JsonPropertyName("used_setsid")] public bool UsedSetsid { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonIgnore] public long? ExitCode { get; set; }
    [JsonPropertyName("log_bytes")] public long LogBytes { get; set; }
    [JsonPropertyName("started_at")] public long StartedAt { get; set; }
    [JsonIgnore] public long? FinishedAt { get; set; }
}

/// <summary>工具调用审计（对应 Go store.Audit；RunIDs 的 json tag 照抄 Go 的 "RunIDs"）。</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public long Ts { get; set; }
    public long? TokenId { get; set; }
    public string? TokenName { get; set; }
    public string Tool { get; set; } = "";
    public string? Host { get; set; }
    public string ParamsJson { get; set; } = "";
    [JsonPropertyName("RunIDs")] public string[]? RunIds { get; set; }
    public bool Ok { get; set; }
    public long? ExitCode { get; set; }
    public long DurationMs { get; set; }
    public long BytesOut { get; set; }
}

/// <summary>
/// 一条用户可见的远程命令执行记录。host/token 名称与 ID 同时快照，避免主机或令牌删除、
/// ID 复用后历史归属发生漂移（对应 Go store.CommandRun，Go 无 json tag，字段名 camelCase）。
/// </summary>
public sealed class CommandRun
{
    public long Seq { get; set; }
    public string Id { get; set; } = "";
    public long? TokenId { get; set; }
    public string? TokenName { get; set; }
    public string Tool { get; set; } = "";
    public long? HostId { get; set; }
    public string Host { get; set; } = "";
    public string Command { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string? Session { get; set; }
    public string? JobId { get; set; }
    public string Status { get; set; } = "";
    public long? ExitCode { get; set; }
    public string StdoutPreview { get; set; } = "";
    public string StderrPreview { get; set; } = "";
    public long StdoutBytes { get; set; }
    public long StderrBytes { get; set; }
    public bool OutputAvailable { get; set; }
    public bool OutputExpired { get; set; }
    public string? OutputError { get; set; }
    public string? ErrorText { get; set; }
    public long StartedAt { get; set; }
    public long? FinishedAt { get; set; }
}

/// <summary>命令执行的完成结果（对应 Go store.CommandRunFinish）。</summary>
public sealed class CommandRunFinish
{
    public string Status { get; set; } = "";
    public long? ExitCode { get; set; }
    public string StdoutPreview { get; set; } = "";
    public string StderrPreview { get; set; } = "";
    public long StdoutBytes { get; set; }
    public long StderrBytes { get; set; }
    public bool OutputAvailable { get; set; }
    public string? OutputError { get; set; }
    public string? ErrorText { get; set; }
    public long FinishedAt { get; set; }
}

/// <summary>命令记录过滤条件（对应 Go store.CommandRunFilter）。</summary>
public sealed class CommandRunFilter
{
    public long[] TokenIds { get; set; } = [];
    public string[] Hosts { get; set; } = [];
    public string[] Tools { get; set; } = [];
    public string[] Statuses { get; set; } = [];
    public string Query { get; set; } = "";
    public long Before { get; set; }
    public int Limit { get; set; }
}

/// <summary>主机资源采样（对应 Go store.Metric）。</summary>
public sealed class HostMetric
{
    public long HostId { get; set; }
    public long Ts { get; set; }
    public double? CpuPct { get; set; }
    public long? MemUsedKb { get; set; }
    public long? MemTotalKb { get; set; }
    public double? Load1 { get; set; }
    public string DisksJson { get; set; } = "";
}

/// <summary>记忆条目（对应 Go store.Memory）。</summary>
public sealed class MemoryRecord
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonIgnore] public long? HostId { get; set; }
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("importance")] public double Importance { get; set; }
    [JsonPropertyName("veracity")] public string Veracity { get; set; } = "";
    [JsonIgnore] public long? TokenId { get; set; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public long UpdatedAt { get; set; }
    [JsonPropertyName("recall_count")] public long RecallCount { get; set; }
    [JsonIgnore] public long? LastRecalled { get; set; }
    [JsonIgnore] public byte[]? Embedding { get; set; }
    [JsonIgnore] public string? EmbeddingModel { get; set; }
}

/// <summary>带 BM25 rank 的记忆（对应 Go store.MemoryWithRank）。</summary>
public sealed class MemoryWithRank
{
    public MemoryRecord Memory { get; set; } = new();
    public double Rank { get; set; }
}

/// <summary>向量索引行（对应 Go store.MemoryVector）。</summary>
public sealed class MemoryVector
{
    public long Id { get; set; }
    public byte[] Embedding { get; set; } = [];
}

/// <summary>记忆管理列表行（对应 Go store.MemoryAdminRow）。</summary>
public sealed class MemoryAdminRow
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("host_id")] public long? HostId { get; set; }
    [JsonPropertyName("host_name")] public string? HostName { get; set; }
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("importance")] public double Importance { get; set; }
    [JsonPropertyName("veracity")] public string Veracity { get; set; } = "";
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public long UpdatedAt { get; set; }
    [JsonPropertyName("recall_count")] public long RecallCount { get; set; }
    [JsonIgnore] public long? LastRecalled { get; set; }
}

/// <summary>单个 bank 的统计（对应 Go store.MemoryBankStat）。</summary>
public sealed class MemoryBankStat
{
    [JsonIgnore] public long? HostId { get; set; }
    [JsonPropertyName("count")] public long Count { get; set; }
    [JsonPropertyName("embedded")] public long Embedded { get; set; }
    [JsonIgnore] public long? LastWritten { get; set; }
}

// ---- OAuth 骨架模型（对应 oauthserver：动态客户端注册 / PKCE；字段名按原版语义 camelCase）----

/// <summary>OAuth 动态注册的客户端。</summary>
public sealed class OAuthClient
{
    [JsonPropertyName("client_id")] public string ClientId { get; set; } = "";
    [JsonPropertyName("client_name")] public string ClientName { get; set; } = "";
    [JsonPropertyName("redirect_uris")] public string[] RedirectUris { get; set; } = [];
    [JsonPropertyName("grant_types")] public string[] GrantTypes { get; set; } = [];
    [JsonPropertyName("token_endpoint_auth_method")] public string TokenEndpointAuthMethod { get; set; } = "";
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
}

/// <summary>授权码（PKCE）。</summary>
public sealed class OAuthCode
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("client_id")] public string ClientId { get; set; } = "";
    [JsonPropertyName("redirect_uri")] public string RedirectUri { get; set; } = "";
    [JsonPropertyName("scope")] public string Scope { get; set; } = "";
    [JsonPropertyName("code_challenge")] public string CodeChallenge { get; set; } = "";
    [JsonPropertyName("code_challenge_method")] public string CodeChallengeMethod { get; set; } = "";
    [JsonPropertyName("token_id")] public long? TokenId { get; set; }
    [JsonPropertyName("expires_at")] public long ExpiresAt { get; set; }
}
