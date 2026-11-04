// OAuth 持久化的最小可�?SQL 实现（对应原�?internal/store/oauth.go + auth_sessions.go �?OAuth 相关部分）�?// 直接�?OAuth 插件内通过 ISubSshStore.ConnectionString 执行 SQL；表结构�?Plugin.Db �?// migration 0004_oauth.sql + 0005_oauth_code_tombstone.sql 提供。待 Db 插件补齐
// IOAuthStoreExtension 后可替换为共享实现（见报告的存取方法清单）。
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.OAuth;

/// <summary>行不存在（映�?Go sql.ErrNoRows）�?/summary>
internal sealed class StoreNotFoundException : Exception;

/// <summary>grant 已被撤销（映�?Go store.ErrOAuthGrantRevoked）�?/summary>
internal sealed class StoreGrantRevokedException(string message) : Exception(message);

public sealed class SqliteOAuthStore(string connectionString){
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout(5000); PRAGMA foreign_keys(ON);";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private static void Bind(SqliteCommand cmd, params (string Name, object? Value)[] args)
    {
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static async Task<SqliteDataReader> QueryAsync(SqliteConnection conn, string sql, (string, object?)[] args, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, args);
        return (SqliteDataReader)await cmd.ExecuteReaderAsync(ct);
    }

    private static object? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? DBNull.Value : value;

    private static long? GetNullableInt64(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static string? GetNullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static string JsonArray(IReadOnlyList<long> ids) => JsonSerializer.Serialize(ids);
    private static string JsonArray(IReadOnlyList<string> values) => JsonSerializer.Serialize(values);

    private static List<long> ParseLongArray(string raw) =>
        string.IsNullOrEmpty(raw) ? [] : (JsonSerializer.Deserialize<List<long>>(raw) ?? []);

    private static List<string> ParseStringArray(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return [];
        var values = JsonSerializer.Deserialize<List<string>>(raw)
            ?? throw new InvalidOperationException("disabled_tools_json must be a JSON array");
        return values;
    }

    // ---- 客户�?----

    public async Task<OAuthClient> CreateOAuthClientAsync(OAuthClient client, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO oauth_clients(client_id,client_name,client_uri,redirect_uris_json,created_at) VALUES($id,$name,$uri,$redirects,$created)";
        var createdAt = client.CreatedAt != 0 ? client.CreatedAt : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Bind(cmd, ("$id", client.ClientId), ("$name", client.ClientName), ("$uri", NullIfEmpty(client.ClientUri)),
            ("$redirects", JsonSerializer.Serialize(client.RedirectUris)), ("$created", createdAt));
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return client with { CreatedAt = createdAt };
    }

    public async Task<OAuthClient> GetOAuthClientAsync(string clientId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var r = await QueryAsync(conn,
            "SELECT client_id,client_name,client_uri,redirect_uris_json,created_at FROM oauth_clients WHERE client_id=$id",
            [("$id", clientId)], ct);
        if (!await r.ReadAsync(ct)) throw new StoreNotFoundException();
        var redirects = ParseStringArray(r.GetString(3));
        return new OAuthClient(r.GetString(0), r.GetString(1), GetNullableString(r, 2) ?? "", redirects, r.GetInt64(4));
    }

    // ---- 授权�?----

    public async Task CreateOAuthAuthorizationCodeAsync(OAuthAuthorizationCode code, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO oauth_authorization_codes(code_hash,client_id,redirect_uri,resource,code_challenge,scope,all_hosts,manage_hosts,host_ids_json,disabled_tools_json,expires_at,created_at)
            VALUES($code_hash,$client_id,$redirect_uri,$resource,$code_challenge,$scope,$all_hosts,$manage_hosts,$host_ids,$disabled_tools,$expires_at,$created_at)
            """;
        var createdAt = code.CreatedAt != 0 ? code.CreatedAt : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Bind(cmd,
            ("$code_hash", code.CodeHash), ("$client_id", code.ClientId), ("$redirect_uri", code.RedirectUri),
            ("$resource", code.Resource), ("$code_challenge", code.CodeChallenge), ("$scope", code.Scope),
            ("$all_hosts", code.AllHosts ? 1 : 0), ("$manage_hosts", code.ManageHosts ? 1 : 0),
            ("$host_ids", JsonArray(code.HostIds)), ("$disabled_tools", JsonArray(code.DisabledTools)),
            ("$expires_at", code.ExpiresAt), ("$created_at", createdAt));
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>原子交换授权码：校验→重放检测（撤销整个 grant）→建访问令�?刷新令牌→标记已用�?/summary>
    public async Task<OAuthAuthorizationCode> ExchangeOAuthAuthorizationCodeAsync(OAuthAuthorizationCodeExchange input, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        try
        {
            OAuthAuthorizationCode code;
            {
                await using var r = await QueryAsync(conn, """
                    SELECT code_hash,client_id,redirect_uri,resource,code_challenge,scope,all_hosts,manage_hosts,host_ids_json,disabled_tools_json,expires_at,created_at,used_at,grant_id
                    FROM oauth_authorization_codes WHERE code_hash=$code_hash
                    """, [("$code_hash", input.CodeHash)], ct);
                if (!await r.ReadAsync(ct)) throw new StoreNotFoundException();
                code = new OAuthAuthorizationCode
                {
                    CodeHash = r.GetString(0),
                    ClientId = r.GetString(1),
                    RedirectUri = r.GetString(2),
                    Resource = r.GetString(3),
                    CodeChallenge = r.GetString(4),
                    Scope = r.GetString(5),
                    AllHosts = r.GetInt64(6) != 0,
                    ManageHosts = r.GetInt64(7) != 0,
                    HostIds = ParseLongArray(r.GetString(8)).ToArray(),
                    DisabledTools = ParseStringArray(r.GetString(9)).ToArray(),
                    ExpiresAt = r.GetInt64(10),
                    CreatedAt = r.GetInt64(11),
                    UsedAt = GetNullableInt64(r, 12),
                    GrantId = GetNullableString(r, 13),
                };
            }

            var valid = ConstantTimeEquals(code.CodeChallenge, input.CodeChallenge)
                && code.ClientId == input.ClientId
                && code.RedirectUri == input.RedirectUri
                && code.Resource == input.Resource
                && code.ExpiresAt > input.Now;
            if (!valid) throw new StoreNotFoundException();

            if (code.UsedAt is not null)
            {
                if (code.GrantId is null)
                    throw new InvalidOperationException("OAuth authorization code tombstone is missing its grant ID");
                await RevokeOAuthGrantAsync(conn, tx, code.GrantId, input.Now, ct);
                await tx.CommitAsync(ct);
                throw new OAuthReuseException("OAuth authorization code reuse detected");
            }

            var accessTokenId = await CreateTokenAsync(conn, tx, new OAuthTokenCreate(
                input.AccessTokenName, input.AccessTokenHash, code.AllHosts, code.ManageHosts,
                code.DisabledTools, code.HostIds, "oauth", input.AccessExpiresAt, code.Resource, code.ClientId), input.Now, ct);

            await InsertOAuthRefreshTokenAsync(conn, tx, new OAuthRefreshToken
            {
                TokenHash = input.RefreshTokenHash,
                GrantId = input.GrantId,
                AccessTokenId = accessTokenId,
                ClientId = code.ClientId,
                Resource = code.Resource,
                Scope = code.Scope,
                AllHosts = code.AllHosts,
                ManageHosts = code.ManageHosts,
                HostIds = code.HostIds,
                DisabledTools = code.DisabledTools,
                ExpiresAt = input.RefreshExpiresAt,
                CreatedAt = input.Now,
            }, ct);

            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE oauth_authorization_codes SET used_at=$used,grant_id=$grant WHERE code_hash=$code_hash AND used_at IS NULL";
                Bind(cmd, ("$used", input.Now), ("$grant", input.GrantId), ("$code_hash", input.CodeHash));
                if (await cmd.ExecuteNonQueryAsync(ct) != 1)
                    throw new OAuthReuseException("OAuth authorization code reuse detected");
            }

            await tx.CommitAsync(ct);
            return code with { UsedAt = input.Now, GrantId = input.GrantId };
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ---- 刷新令牌 ----

    public async Task<OAuthRefreshToken> RotateOAuthRefreshTokenAsync(OAuthRefreshTokenRotation input, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        try
        {
            OAuthRefreshToken token;
            {
                await using var r = await QueryAsync(conn, """
                    SELECT token_hash,grant_id,access_token_id,client_id,resource,scope,all_hosts,manage_hosts,host_ids_json,disabled_tools_json,expires_at,created_at,used_at,revoked_at
                    FROM oauth_refresh_tokens WHERE token_hash=$token_hash
                    """, [("$token_hash", input.TokenHash)], ct);
                if (!await r.ReadAsync(ct)) throw new StoreNotFoundException();
                token = new OAuthRefreshToken
                {
                    TokenHash = r.GetString(0),
                    GrantId = r.GetString(1),
                    AccessTokenId = r.GetInt64(2),
                    ClientId = r.GetString(3),
                    Resource = r.GetString(4),
                    Scope = r.GetString(5),
                    AllHosts = r.GetInt64(6) != 0,
                    ManageHosts = r.GetInt64(7) != 0,
                    HostIds = ParseLongArray(r.GetString(8)).ToArray(),
                    DisabledTools = ParseStringArray(r.GetString(9)).ToArray(),
                    ExpiresAt = r.GetInt64(10),
                    CreatedAt = r.GetInt64(11),
                    UsedAt = GetNullableInt64(r, 12),
                    RevokedAt = GetNullableInt64(r, 13),
                };
            }

            if (token.ClientId != input.ClientId || token.Resource != input.Resource
                || token.ExpiresAt <= input.Now || token.RevokedAt is not null)
                throw new StoreNotFoundException();

            if (token.UsedAt is not null)
            {
                await RevokeOAuthGrantAsync(conn, tx, token.GrantId, input.Now, ct);
                await tx.CommitAsync(ct);
                throw new OAuthReuseException("OAuth refresh token reuse detected");
            }

            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE oauth_refresh_tokens SET used_at=$used WHERE token_hash=$token_hash AND used_at IS NULL AND revoked_at IS NULL";
                Bind(cmd, ("$used", input.Now), ("$token_hash", input.TokenHash));
                if (await cmd.ExecuteNonQueryAsync(ct) != 1)
                    throw new OAuthReuseException("OAuth refresh token reuse detected");
            }

            var accessTokenId = await CreateTokenAsync(conn, tx, new OAuthTokenCreate(
                input.AccessTokenName, input.AccessTokenHash, token.AllHosts, token.ManageHosts,
                token.DisabledTools, token.HostIds, "oauth", input.AccessExpiresAt, token.Resource, token.ClientId), input.Now, ct);

            await InsertOAuthRefreshTokenAsync(conn, tx, new OAuthRefreshToken
            {
                TokenHash = input.RefreshTokenHash,
                GrantId = token.GrantId,
                AccessTokenId = accessTokenId,
                ClientId = token.ClientId,
                Resource = token.Resource,
                Scope = token.Scope,
                AllHosts = token.AllHosts,
                ManageHosts = token.ManageHosts,
                HostIds = token.HostIds,
                DisabledTools = token.DisabledTools,
                ExpiresAt = input.RefreshExpiresAt,
                CreatedAt = input.Now,
            }, ct);

            await tx.CommitAsync(ct);
            return token with { UsedAt = input.Now };
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task InsertOAuthRefreshTokenAsync(SqliteConnection conn, SqliteTransaction tx, OAuthRefreshToken token, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO oauth_refresh_tokens(token_hash,grant_id,access_token_id,client_id,resource,scope,all_hosts,manage_hosts,host_ids_json,disabled_tools_json,expires_at,created_at,used_at,revoked_at)
            VALUES($token_hash,$grant_id,$access_token_id,$client_id,$resource,$scope,$all_hosts,$manage_hosts,$host_ids,$disabled_tools,$expires_at,$created_at,$used_at,$revoked_at)
            """;
        Bind(cmd,
            ("$token_hash", token.TokenHash), ("$grant_id", token.GrantId), ("$access_token_id", token.AccessTokenId),
            ("$client_id", token.ClientId), ("$resource", token.Resource), ("$scope", token.Scope),
            ("$all_hosts", token.AllHosts ? 1 : 0), ("$manage_hosts", token.ManageHosts ? 1 : 0),
            ("$host_ids", JsonArray(token.HostIds)), ("$disabled_tools", JsonArray(token.DisabledTools)),
            ("$expires_at", token.ExpiresAt), ("$created_at", token.CreatedAt),
            ("$used_at", token.UsedAt), ("$revoked_at", token.RevokedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task RevokeOAuthGrantAsync(SqliteConnection conn, SqliteTransaction tx, string grantId, long now, CancellationToken ct)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE tokens SET expires_at=$now WHERE id IN (SELECT access_token_id FROM oauth_refresh_tokens WHERE grant_id=$grant_id)";
            Bind(cmd, ("$now", now), ("$grant_id", grantId));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE oauth_refresh_tokens SET revoked_at=$now WHERE grant_id=$grant_id AND revoked_at IS NULL";
            Bind(cmd, ("$now", now), ("$grant_id", grantId));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>建访问令牌（对应 Go createTokenTx：tokens + token_hosts）�?/summary>
    private static async Task<long> CreateTokenAsync(SqliteConnection conn, SqliteTransaction tx, OAuthTokenCreate input, long now, CancellationToken ct)
    {
        long id;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO tokens(name,token_hash,all_hosts,manage_hosts,disabled_tools_json,created_at,source,expires_at,resource,client_id)
                VALUES($name,$hash,$all_hosts,$manage_hosts,$disabled_tools,$created_at,$source,$expires_at,$resource,$client_id)
                """;
            Bind(cmd,
                ("$name", input.Name), ("$hash", input.Hash),
                ("$all_hosts", input.AllHosts ? 1 : 0), ("$manage_hosts", input.ManageHosts ? 1 : 0),
                ("$disabled_tools", JsonArray(input.DisabledTools)), ("$created_at", now), ("$source", "oauth"),
                ("$expires_at", input.ExpiresAt > 0 ? input.ExpiresAt : null),
                ("$resource", NullIfEmpty(input.Resource)), ("$client_id", NullIfEmpty(input.ClientId)));
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        foreach (var hostId in input.HostIds)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO token_hosts(token_id,host_id) VALUES($token_id,$host_id)";
            Bind(cmd, ("$token_id", id), ("$host_id", hostId));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return id;
    }

    private sealed record OAuthTokenCreate(
        string Name, string Hash, bool AllHosts, bool ManageHosts,
        IReadOnlyList<string> DisabledTools, IReadOnlyList<long> HostIds,
        string Source, long ExpiresAt, string Resource, string ClientId);

    // ---- 主机 ----

    public async Task<IReadOnlyList<long>> ValidateHostIdsAsync(IReadOnlyList<long> hostIds, CancellationToken ct = default)
    {
        var seen = new HashSet<long>();
        var unique = new List<long>();
        await using var conn = await OpenAsync(ct);
        foreach (var hostId in hostIds)
        {
            if (hostId <= 0) throw new InvalidOperationException("主机 ID 无效");
            if (!seen.Add(hostId)) continue;
            await using var r = await QueryAsync(conn, "SELECT 1 FROM hosts WHERE id=$id", [("$id", hostId)], ct);
            if (!await r.ReadAsync(ct)) throw new InvalidOperationException("所选主机不存在");
            unique.Add(hostId);
        }
        return unique;
    }

    /// <summary>授权页主机列表：归档主机不可见（对应 ListHosts + Host.View 的过滤结果）�?/summary>
    public async Task<IReadOnlyList<OAuthHostView>> ListHostViewsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var r = await QueryAsync(conn, """
            SELECT id,name,addr,port,username,auth_type,key_id,hostkey_fp,jump_host_id,monitor_enabled,tags_json,created_at
            FROM hosts WHERE archived_at IS NULL ORDER BY name
            """, [], ct);
        var views = new List<OAuthHostView>();
        while (await r.ReadAsync(ct))
        {
            var tagsJson = GetNullableString(r, 10);
            var tags = string.IsNullOrEmpty(tagsJson) ? [] : (JsonSerializer.Deserialize<List<string>>(tagsJson) ?? []);
            views.Add(new OAuthHostView(
                r.GetInt64(0), r.GetString(1), r.GetString(2), (int)r.GetInt64(3), r.GetString(4), r.GetString(5),
                GetNullableInt64(r, 6), GetNullableString(r, 7), GetNullableInt64(r, 8),
                r.GetInt64(9) != 0, tags, r.GetInt64(11)));
        }
        return views;
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        var bytesA = Encoding.UTF8.GetBytes(a);
        var bytesB = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }
}
