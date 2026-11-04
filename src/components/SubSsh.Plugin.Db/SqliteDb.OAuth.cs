// SubSsh.Plugin.Db — OAuth 存储（对应 Go internal/store/oauth.go）。
// 记录类型（OAuthAuthorizationCode/OAuthAuthorizationCodeExchange/OAuthRefreshToken/OAuthRefreshTokenRotation/OAuthClient）
// 由并行任务定义在 SubSsh.Core.Abstractions.OAuthAbstractions.cs，此处直接复用；重放语义抛 OAuthReuseException。
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.Db;

public sealed partial class SqliteDb
{
    public Task<OAuthClientRecord> CreateOAuthClientAsync(OAuthClientRecord client, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var createdAt = client.CreatedAt == 0 ? Now() : client.CreatedAt;
            Exec("INSERT INTO oauth_clients(client_id,client_name,client_uri,redirect_uris_json,created_at) VALUES(?,?,?,?,?)",
                client.ClientId, client.ClientName, NullableString(client.ClientUri), JsonSerializer.Serialize(client.RedirectUris), createdAt);
            return Task.FromResult(new OAuthClientRecord
            {
                ClientId = client.ClientId,
                ClientName = client.ClientName,
                ClientUri = client.ClientUri,
                RedirectUris = client.RedirectUris,
                CreatedAt = createdAt,
            });
        }
    }

    public Task<OAuthClientRecord?> GetOAuthClientAsync(string clientId, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var rd = Query("SELECT client_id,client_name,client_uri,redirect_uris_json,created_at FROM oauth_clients WHERE client_id=?", clientId);
            if (!rd.Read()) return Task.FromResult<OAuthClientRecord?>(null);
            return Task.FromResult<OAuthClientRecord?>(new OAuthClientRecord
            {
                ClientId = SN(rd, 0),
                ClientName = SN(rd, 1),
                ClientUri = ST(rd, 2) ?? "",
                RedirectUris = JsonSerializer.Deserialize<string[]>(SN(rd, 3)) ?? [],
                CreatedAt = IL(rd, 4),
            });
        }
    }

    public Task CreateOAuthAuthorizationCodeAsync(OAuthAuthorizationCode code, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var (disabledJson, _) = EncodeDisabledTools(code.DisabledTools);
            var createdAt = code.CreatedAt == 0 ? Now() : code.CreatedAt;
            Exec(@"INSERT INTO oauth_authorization_codes(code_hash,client_id,redirect_uri,resource,code_challenge,scope,all_hosts,manage_hosts,host_ids_json,disabled_tools_json,expires_at,created_at)
                VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
                code.CodeHash, code.ClientId, code.RedirectUri, code.Resource, code.CodeChallenge, code.Scope,
                code.AllHosts ? 1 : 0, code.ManageHosts ? 1 : 0, JsonSerializer.Serialize(code.HostIds), disabledJson,
                code.ExpiresAt, createdAt);
        }
        return Task.CompletedTask;
    }

    /// <summary>授权码交换：协议级无效返回 null；检测到重放时抛 OAuthReuseException（整个 grant 已撤销）。</summary>
    public Task<OAuthAuthorizationCode?> ExchangeOAuthAuthorizationCodeAsync(OAuthAuthorizationCodeExchange input, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var tx = _conn.BeginTransaction();
            OAuthAuthorizationCode code;
            using (var rd = Query(@"SELECT code_hash,client_id,redirect_uri,resource,code_challenge,scope,all_hosts,manage_hosts,host_ids_json,disabled_tools_json,expires_at,created_at,used_at,grant_id
                FROM oauth_authorization_codes WHERE code_hash=?", input.CodeHash))
            {
                if (!rd.Read())
                {
                    tx.Rollback();
                    return Task.FromResult<OAuthAuthorizationCode?>(null);
                }
                code = new OAuthAuthorizationCode
                {
                    CodeHash = SN(rd, 0),
                    ClientId = SN(rd, 1),
                    RedirectUri = SN(rd, 2),
                    Resource = SN(rd, 3),
                    CodeChallenge = SN(rd, 4),
                    Scope = SN(rd, 5),
                    AllHosts = BO(rd, 6),
                    ManageHosts = BO(rd, 7),
                    HostIds = JsonSerializer.Deserialize<long[]>(SN(rd, 8)) ?? [],
                    DisabledTools = DecodeDisabledTools(SN(rd, 9)),
                    ExpiresAt = IL(rd, 10),
                    CreatedAt = IL(rd, 11),
                    UsedAt = NL(rd, 12),
                    GrantId = ST(rd, 13),
                };
            }
            var valid = ConstantTimeEquals(code.CodeChallenge, input.CodeChallenge)
                && code.ClientId == input.ClientId
                && code.RedirectUri == input.RedirectUri
                && code.Resource == input.Resource
                && code.ExpiresAt > input.Now;
            if (!valid)
            {
                tx.Rollback();
                return Task.FromResult<OAuthAuthorizationCode?>(null);
            }
            if (code.UsedAt != null)
            {
                if (code.GrantId == null)
                {
                    throw new SubSshException("OAuth authorization code tombstone is missing its grant ID");
                }
                RevokeOAuthGrantTx(tx, code.GrantId, input.Now);
                tx.Commit();
                throw new OAuthReuseException("OAuth authorization code reuse detected");
            }

            var accessToken = CreateTokenTx(tx, new TokenCreate
            {
                Name = input.AccessTokenName,
                Hash = input.AccessTokenHash,
                AllHosts = code.AllHosts,
                ManageHosts = code.ManageHosts,
                DisabledTools = code.DisabledTools.ToArray(),
                HostIds = code.HostIds.ToArray(),
                Source = "oauth",
                ExpiresAt = input.AccessExpiresAt,
                Resource = code.Resource,
                ClientId = code.ClientId,
            }, input.Now);
            InsertOAuthRefreshTokenTx(tx, new OAuthRefreshToken
            {
                TokenHash = input.RefreshTokenHash,
                GrantId = input.GrantId,
                AccessTokenId = accessToken.Id,
                ClientId = code.ClientId,
                Resource = code.Resource,
                Scope = code.Scope,
                AllHosts = code.AllHosts,
                ManageHosts = code.ManageHosts,
                DisabledTools = code.DisabledTools,
                HostIds = code.HostIds,
                ExpiresAt = input.RefreshExpiresAt,
                CreatedAt = input.Now,
            });
            using (var cmd = tx.Connection!.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE oauth_authorization_codes SET used_at=?,grant_id=? WHERE code_hash=? AND used_at IS NULL";
                Bind(cmd, [input.Now, input.GrantId, input.CodeHash]);
                if (cmd.ExecuteNonQuery() != 1)
                {
                    throw new OAuthReuseException("OAuth authorization code reuse detected");
                }
            }
            tx.Commit();
            return Task.FromResult<OAuthAuthorizationCode?>(code with { UsedAt = input.Now, GrantId = input.GrantId });
        }
    }

    public Task CreateOAuthRefreshTokenAsync(OAuthRefreshTokenRecord token, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (token.GrantId.Length == 0)
            {
                throw new SubSshException("OAuth grant ID 不能为空");
            }
            var createdAt = token.CreatedAt == 0 ? Now() : token.CreatedAt;
            using var tx = _conn.BeginTransaction();
            var revoked = ScalarLong("SELECT count(*) FROM oauth_refresh_tokens WHERE grant_id=? AND revoked_at IS NOT NULL", tx, token.GrantId);
            if (revoked != 0)
            {
                throw new SubSshException("OAuth grant revoked");
            }
            InsertOAuthRefreshTokenTx(tx, new OAuthRefreshToken
            {
                TokenHash = token.TokenHash,
                GrantId = token.GrantId,
                AccessTokenId = token.AccessTokenId,
                ClientId = token.ClientId,
                Resource = token.Resource,
                Scope = token.Scope,
                AllHosts = token.AllHosts,
                ManageHosts = token.ManageHosts,
                DisabledTools = token.DisabledTools,
                HostIds = token.HostIds,
                ExpiresAt = token.ExpiresAt,
                CreatedAt = createdAt,
                UsedAt = token.UsedAt,
                RevokedAt = token.RevokedAt,
            });
            tx.Commit();
        }
        return Task.CompletedTask;
    }

    /// <summary>刷新令牌轮换：协议级无效返回 null；检测到重放时抛 OAuthReuseException。</summary>
    public Task<OAuthRefreshToken?> RotateOAuthRefreshTokenAsync(OAuthRefreshTokenRotation input, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var tx = _conn.BeginTransaction();
            OAuthRefreshToken token;
            using (var rd = Query(@"SELECT token_hash,grant_id,access_token_id,client_id,resource,scope,all_hosts,manage_hosts,host_ids_json,disabled_tools_json,expires_at,created_at,used_at,revoked_at
                FROM oauth_refresh_tokens WHERE token_hash=?", input.TokenHash))
            {
                if (!rd.Read())
                {
                    tx.Rollback();
                    return Task.FromResult<OAuthRefreshToken?>(null);
                }
                token = new OAuthRefreshToken
                {
                    TokenHash = SN(rd, 0),
                    GrantId = SN(rd, 1),
                    AccessTokenId = IL(rd, 2),
                    ClientId = SN(rd, 3),
                    Resource = SN(rd, 4),
                    Scope = SN(rd, 5),
                    AllHosts = BO(rd, 6),
                    ManageHosts = BO(rd, 7),
                    HostIds = JsonSerializer.Deserialize<long[]>(SN(rd, 8)) ?? [],
                    DisabledTools = DecodeDisabledTools(SN(rd, 9)),
                    ExpiresAt = IL(rd, 10),
                    CreatedAt = IL(rd, 11),
                    UsedAt = NL(rd, 12),
                    RevokedAt = NL(rd, 13),
                };
            }
            if (token.ClientId != input.ClientId || token.Resource != input.Resource || token.ExpiresAt <= input.Now || token.RevokedAt != null)
            {
                tx.Rollback();
                return Task.FromResult<OAuthRefreshToken?>(null);
            }
            if (token.UsedAt != null)
            {
                RevokeOAuthGrantTx(tx, token.GrantId, input.Now);
                tx.Commit();
                throw new OAuthReuseException("OAuth refresh token reuse detected");
            }
            using (var cmd = tx.Connection!.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE oauth_refresh_tokens SET used_at=? WHERE token_hash=? AND used_at IS NULL AND revoked_at IS NULL";
                Bind(cmd, [input.Now, input.TokenHash]);
                if (cmd.ExecuteNonQuery() != 1)
                {
                    throw new OAuthReuseException("OAuth refresh token reuse detected");
                }
            }
            var accessToken = CreateTokenTx(tx, new TokenCreate
            {
                Name = input.AccessTokenName,
                Hash = input.AccessTokenHash,
                AllHosts = token.AllHosts,
                ManageHosts = token.ManageHosts,
                DisabledTools = token.DisabledTools.ToArray(),
                HostIds = token.HostIds.ToArray(),
                Source = "oauth",
                ExpiresAt = input.AccessExpiresAt,
                Resource = token.Resource,
                ClientId = token.ClientId,
            }, input.Now);
            InsertOAuthRefreshTokenTx(tx, new OAuthRefreshToken
            {
                TokenHash = input.RefreshTokenHash,
                GrantId = token.GrantId,
                AccessTokenId = accessToken.Id,
                ClientId = token.ClientId,
                Resource = token.Resource,
                Scope = token.Scope,
                AllHosts = token.AllHosts,
                ManageHosts = token.ManageHosts,
                DisabledTools = token.DisabledTools,
                HostIds = token.HostIds,
                ExpiresAt = input.RefreshExpiresAt,
                CreatedAt = input.Now,
            });
            tx.Commit();
            return Task.FromResult<OAuthRefreshToken?>(token with { UsedAt = input.Now });
        }
    }

    /// <summary>三处共用的刷新令牌写入，保证列顺序与序列化方式一致。</summary>
    private void InsertOAuthRefreshTokenTx(SqliteTransaction tx, OAuthRefreshToken token)
    {
        var (disabledJson, _) = EncodeDisabledTools(token.DisabledTools);
        Exec(tx, @"INSERT INTO oauth_refresh_tokens(token_hash,grant_id,access_token_id,client_id,resource,scope,all_hosts,manage_hosts,host_ids_json,disabled_tools_json,expires_at,created_at,used_at,revoked_at)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
            token.TokenHash, token.GrantId, token.AccessTokenId, token.ClientId, token.Resource, token.Scope,
            token.AllHosts ? 1 : 0, token.ManageHosts ? 1 : 0, JsonSerializer.Serialize(token.HostIds), disabledJson,
            token.ExpiresAt, token.CreatedAt, token.UsedAt, token.RevokedAt);
    }

    private static void RevokeOAuthGrantTx(SqliteTransaction tx, string grantId, long now)
    {
        Exec(tx, "UPDATE tokens SET expires_at=? WHERE id IN (SELECT access_token_id FROM oauth_refresh_tokens WHERE grant_id=?)", now, grantId);
        Exec(tx, "UPDATE oauth_refresh_tokens SET revoked_at=? WHERE grant_id=? AND revoked_at IS NULL", now, grantId);
    }

    /// <summary>校验并去重主机 ID；无效/不存在时抛 SubSshException（文案与 Go 版一致）。</summary>
    public Task<IReadOnlyList<long>> ValidateHostIdsAsync(IReadOnlyList<long> hostIds, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var seen = new HashSet<long>();
            var unique = new List<long>(hostIds.Count);
            foreach (var hostId in hostIds)
            {
                if (hostId <= 0)
                {
                    throw new SubSshException("主机 ID 无效");
                }
                if (!seen.Add(hostId)) continue;
                using var rd = Query("SELECT 1 FROM hosts WHERE id=?", hostId);
                if (!rd.Read())
                {
                    throw new SubSshException("所选主机不存在");
                }
                unique.Add(hostId);
            }
            return Task.FromResult<IReadOnlyList<long>>(unique);
        }
    }

    /// <summary>Go subtle.ConstantTimeCompare 的等价物（恒时比较）。</summary>
    private static bool ConstantTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
