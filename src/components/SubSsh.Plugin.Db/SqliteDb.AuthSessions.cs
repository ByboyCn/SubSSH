// SubSsh.Plugin.Db — tokens & sessions（对应 Go internal/store/auth_sessions.go）。
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.Db;

public sealed partial class SqliteDb
{
    private const string TokenColumns =
        "id,name,all_hosts,manage_hosts,disabled_tools_json,source,created_at,last_used_at,expires_at,resource,client_id";

    private static ApiToken ScanToken(SqliteDataReader rd)
    {
        var t = new ApiToken
        {
            Id = IL(rd, 0),
            Name = SN(rd, 1),
            AllHosts = BO(rd, 2),
            ManageHosts = BO(rd, 3),
            DisabledTools = DecodeDisabledTools(SN(rd, 4)),
            Source = SN(rd, 5),
            CreatedAt = IL(rd, 6),
            LastUsedAt = NL(rd, 7),
            ExpiresAt = NL(rd, 8),
            Resource = ST(rd, 9),
            ClientId = ST(rd, 10),
        };
        return t;
    }

    public Task<ApiToken> CreateTokenAsync(TokenCreate input, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var tx = _conn.BeginTransaction();
            var token = CreateTokenTx(tx, input, Now());
            tx.Commit();
            return Task.FromResult(token);
        }
    }

    private ApiToken CreateTokenTx(SqliteTransaction tx, TokenCreate input, long now)
    {
        var source = input.Source.Length == 0 ? "manual" : input.Source;
        object? expiresAt = input.ExpiresAt > 0 ? input.ExpiresAt : null;
        var (disabledJson, disabledTools) = EncodeDisabledTools(input.DisabledTools ?? []);
        Exec(tx, @"INSERT INTO tokens(name,token_hash,all_hosts,manage_hosts,disabled_tools_json,created_at,source,expires_at,resource,client_id)
            VALUES(?,?,?,?,?,?,?,?,?,?)",
            input.Name, input.Hash, input.AllHosts ? 1 : 0, input.ManageHosts ? 1 : 0, disabledJson,
            now, source, expiresAt, NullableString(input.Resource), NullableString(input.ClientId));
        var id = ScalarLong("SELECT last_insert_rowid()", tx);
        foreach (var hostId in input.HostIds ?? [])
        {
            Exec(tx, "INSERT INTO token_hosts(token_id,host_id) VALUES(?,?)", id, hostId);
        }
        return new ApiToken
        {
            Id = id,
            Name = input.Name,
            AllHosts = input.AllHosts,
            ManageHosts = input.ManageHosts,
            DisabledTools = disabledTools,
            Source = source,
            CreatedAt = now,
            ExpiresAt = input.ExpiresAt > 0 ? input.ExpiresAt : null,
            Resource = input.Resource.Length > 0 ? input.Resource : null,
            ClientId = input.ClientId.Length > 0 ? input.ClientId : null,
        };
    }

    private static object? NullableString(string value) => value.Length == 0 ? null : value;

    public Task<(ApiToken? Token, IReadOnlyList<HostConfig> Hosts)> FindTokenAsync(string hash, CancellationToken ct = default)
        => FindTokenInternalAsync(hash, "", false);

    public Task<(ApiToken? Token, IReadOnlyList<HostConfig> Hosts)> FindTokenForResourceAsync(string hash, string resource, CancellationToken ct = default)
        => FindTokenInternalAsync(hash, resource, true);

    private Task<(ApiToken? Token, IReadOnlyList<HostConfig> Hosts)> FindTokenInternalAsync(string hash, string resource, bool requireResource)
    {
        lock (_sync)
        {
            var q = "SELECT " + TokenColumns + " FROM tokens WHERE token_hash=? AND (expires_at IS NULL OR expires_at>?)";
            var args = new List<object?> { hash, Now() };
            if (requireResource)
            {
                q += " AND (source='manual' OR resource=?)";
                args.Add(resource);
            }
            ApiToken t;
            using (var rd = Query(q, args.ToArray()))
            {
                if (!rd.Read())
                {
                    return Task.FromResult<(ApiToken?, IReadOnlyList<HostConfig>)>((null, Array.Empty<HostConfig>()));
                }
                t = ScanToken(rd);
            }
            Exec("UPDATE tokens SET last_used_at=? WHERE id=?", Now(), t.Id);
            // 归档主机对所有令牌都不可见：在这里过滤即覆盖 hosts_list 与全部按主机鉴权的 MCP 工具。
            string hostQuery = "SELECT " + HostColumns + " FROM hosts h";
            var hostArgs = new List<object?>();
            if (!t.AllHosts)
            {
                hostQuery += " JOIN token_hosts th ON th.host_id=h.id WHERE th.token_id=? AND h.archived_at IS NULL";
                hostArgs.Add(t.Id);
            }
            else
            {
                hostQuery += " WHERE h.archived_at IS NULL";
            }
            hostQuery += " ORDER BY h.name";
            var hosts = new List<HostConfig>();
            using (var rd = Query(hostQuery, hostArgs.ToArray()))
            {
                while (rd.Read())
                {
                    hosts.Add(ScanHost(rd));
                }
            }
            return Task.FromResult<(ApiToken?, IReadOnlyList<HostConfig>)>((t, hosts));
        }
    }

    public Task<IReadOnlyList<ApiToken>> ListTokensAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            var out_ = new List<ApiToken>();
            using var rd = Query("SELECT " + TokenColumns + " FROM tokens ORDER BY name");
            while (rd.Read())
            {
                out_.Add(ScanToken(rd));
            }
            return Task.FromResult<IReadOnlyList<ApiToken>>(out_);
        }
    }

    public Task<ApiToken> UpdateTokenAsync(long id, TokenUpdate input, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var tx = _conn.BeginTransaction();
            var (disabledJson, _) = EncodeDisabledTools(input.DisabledTools ?? []);
            using (var cmd = tx.Connection!.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE tokens SET name=?,all_hosts=?,manage_hosts=?,disabled_tools_json=? WHERE id=?";
                Bind(cmd, [input.Name, input.AllHosts ? 1 : 0, input.ManageHosts ? 1 : 0, disabledJson, id]);
                if (cmd.ExecuteNonQuery() == 0)
                {
                    throw new SubSshException("记录不存在");
                }
            }
            Exec(tx, "DELETE FROM token_hosts WHERE token_id=?", id);
            foreach (var hostId in input.HostIds ?? [])
            {
                Exec(tx, "INSERT INTO token_hosts(token_id,host_id) VALUES(?,?)", id, hostId);
            }
            Exec(tx,
                "UPDATE oauth_refresh_tokens SET all_hosts=?,manage_hosts=?,host_ids_json=?,disabled_tools_json=? WHERE access_token_id=? AND used_at IS NULL AND revoked_at IS NULL",
                input.AllHosts ? 1 : 0, input.ManageHosts ? 1 : 0, MustJson(input.HostIds), disabledJson, id);
            tx.Commit();
        }
        return GetTokenAsync(id)!;
    }

    public Task<ApiToken?> GetTokenAsync(long id, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var rd = Query("SELECT " + TokenColumns + " FROM tokens WHERE id=?", id);
            return Task.FromResult(rd.Read() ? ScanToken(rd) : null);
        }
    }

    public Task DeleteTokenAsync(long id, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var tx = _conn.BeginTransaction();
            string? grantId = null;
            using (var rd = Query("SELECT grant_id FROM oauth_refresh_tokens WHERE access_token_id=? LIMIT 1", id))
            {
                if (rd.Read()) grantId = rd.IsDBNull(0) ? null : rd.GetString(0);
            }
            if (grantId != null)
            {
                RevokeOAuthGrantTx(tx, grantId, Now());
            }
            Exec(tx, "DELETE FROM token_hosts WHERE token_id=?", id);
            Exec(tx, "DELETE FROM tokens WHERE id=?", id);
            tx.Commit();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<long>> TokenHostIdsAsync(long id, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var out_ = new List<long>();
            using var rd = Query("SELECT host_id FROM token_hosts WHERE token_id=?", id);
            while (rd.Read()) out_.Add(rd.GetInt64(0));
            return Task.FromResult<IReadOnlyList<long>>(out_);
        }
    }

    public Task<SessionRecord> GetSessionAsync(long tokenId, long hostId, string label, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var rd = Query("SELECT id,token_id,host_id,label,cwd,env_json,updated_at FROM sessions WHERE token_id=? AND host_id=? AND label=?",
                tokenId, hostId, label);
            if (rd.Read())
            {
                var raw = rd.GetString(5);
                return Task.FromResult(new SessionRecord
                {
                    Id = IL(rd, 0),
                    TokenId = IL(rd, 1),
                    HostId = IL(rd, 2),
                    Label = SN(rd, 3),
                    Cwd = SN(rd, 4),
                    Env = JsonSerializer.Deserialize<Dictionary<string, string>>(raw) ?? [],
                    UpdatedAt = IL(rd, 6),
                });
            }
            // Go 版：查不到时返回默认 session（cwd="~"、空 env）。
            return Task.FromResult(new SessionRecord { TokenId = tokenId, HostId = hostId, Label = label, Cwd = "~", Env = [] });
        }
    }

    public Task SaveSessionAsync(SessionRecord x, CancellationToken ct = default)
    {
        lock (_sync)
        {
            Exec(@"INSERT INTO sessions(token_id,host_id,label,cwd,env_json,updated_at) VALUES(?,?,?,?,?,?)
                ON CONFLICT(token_id,host_id,label) DO UPDATE SET cwd=excluded.cwd,env_json=excluded.env_json,updated_at=excluded.updated_at",
                x.TokenId, x.HostId, x.Label, x.Cwd, JsonSerializer.Serialize(x.Env), Now());
        }
        return Task.CompletedTask;
    }
}
