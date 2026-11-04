// SubSsh.Plugin.Db — keys & hosts（对应 Go internal/store/hosts_keys.go）。
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.Db;

public sealed partial class SqliteDb
{
    /// <summary>scanHost 期望的列顺序，所有读取主机的查询共用。</summary>
    private const string HostColumns =
        "id,name,addr,port,username,auth_type,key_id,password_enc,hostkey_fp,jump_host_id,monitor_enabled,created_at,tags,archived_at";

    private static HostConfig ScanHost(SqliteDataReader rd)
    {
        var h = new HostConfig
        {
            Id = IL(rd, 0),
            Name = SN(rd, 1),
            Addr = SN(rd, 2),
            Port = II(rd, 3),
            Username = SN(rd, 4),
            AuthType = SN(rd, 5),
            KeyId = NL(rd, 6),
            PasswordEnc = BL(rd, 7),
            HostKeyFp = ST(rd, 8),
            JumpHostId = NL(rd, 9),
            MonitorEnabled = BO(rd, 10),
            CreatedAt = IL(rd, 11),
            Tags = ParseHostTags(rd.IsDBNull(12) ? null : rd.GetString(12)),
            ArchivedAt = NL(rd, 13),
        };
        return h;
    }

    private static string HostTagsJson(string[] tags)
    {
        if (tags.Length == 0) return "[]";
        try { return JsonSerializer.Serialize(tags); }
        catch { return "[]"; }
    }

    /// <summary>容忍 NULL、空串与脏数据，始终返回非 null 数组。</summary>
    private static string[] ParseHostTags(string? raw)
    {
        if (raw == null) return [];
        try
        {
            var tags = JsonSerializer.Deserialize<string[]>(raw);
            return tags ?? [];
        }
        catch { return []; }
    }

    public Task<SshKey> CreateKeyAsync(string name, byte[] enc, string publicKey, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var now = Now();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO keys(name,private_key_enc,public_key,created_at) VALUES(?,?,?,?)";
            Bind(cmd, [name, enc, publicKey, now]);
            cmd.ExecuteNonQuery();
            var id = ScalarLong("SELECT last_insert_rowid()");
            return Task.FromResult(new SshKey { Id = id, Name = name, PrivateKeyEnc = enc, PublicKey = publicKey, CreatedAt = now });
        }
    }

    public Task<IReadOnlyList<SshKey>> ListKeysAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            var out_ = new List<SshKey>();
            using (var rd = Query("SELECT id,name,public_key,created_at FROM keys ORDER BY name"))
            {
                while (rd.Read())
                {
                    out_.Add(new SshKey { Id = IL(rd, 0), Name = SN(rd, 1), PublicKey = SN(rd, 2), CreatedAt = IL(rd, 3) });
                }
            }
            return Task.FromResult<IReadOnlyList<SshKey>>(out_);
        }
    }

    public Task<SshKey?> GetKeyAsync(long id, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var rd = Query("SELECT id,name,private_key_enc,public_key,created_at FROM keys WHERE id=?", id);
            if (!rd.Read()) return Task.FromResult<SshKey?>(null);
            return Task.FromResult<SshKey?>(new SshKey
            {
                Id = IL(rd, 0), Name = SN(rd, 1), PrivateKeyEnc = BL(rd, 2)!, PublicKey = SN(rd, 3), CreatedAt = IL(rd, 4),
            });
        }
    }

    public Task DeleteKeyAsync(long id, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var n = ScalarLong("SELECT count(*) FROM hosts WHERE key_id=?", id);
            if (n > 0)
            {
                throw new SubSshException($"密钥正被 {n} 个主机引用");
            }
            Exec("DELETE FROM keys WHERE id=?", id);
        }
        return Task.CompletedTask;
    }

    public Task<HostConfig> CreateHostAsync(HostConfig h, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var now = Now();
            Exec(@"INSERT INTO hosts(name,addr,port,username,auth_type,key_id,password_enc,jump_host_id,monitor_enabled,created_at,tags)
                VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                h.Name, h.Addr, h.Port, h.Username, h.AuthType, h.KeyId, h.PasswordEnc, h.JumpHostId,
                h.MonitorEnabled ? 1 : 0, now, HostTagsJson(h.Tags));
            h.Id = ScalarLong("SELECT last_insert_rowid()");
            h.CreatedAt = now;
            return Task.FromResult(h);
        }
    }

    public Task UpdateHostAsync(HostConfig h, CancellationToken ct = default)
    {
        lock (_sync)
        {
            Exec(@"UPDATE hosts SET name=?,addr=?,port=?,username=?,auth_type=?,key_id=?,password_enc=?,jump_host_id=?,monitor_enabled=?,tags=? WHERE id=?",
                h.Name, h.Addr, h.Port, h.Username, h.AuthType, h.KeyId, h.PasswordEnc, h.JumpHostId,
                h.MonitorEnabled ? 1 : 0, HostTagsJson(h.Tags), h.Id);
        }
        return Task.CompletedTask;
    }

    public Task<bool> DeleteHostAsync(long id, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var tx = _conn.BeginTransaction();
            var running = ScalarLong("SELECT count(*) FROM jobs WHERE host_id=? AND status='running'", tx, id);
            if (running != 0)
            {
                throw new SubSshException("主机存在运行中的后台任务");
            }
            var dependents = ScalarLong("SELECT count(*) FROM hosts WHERE jump_host_id=?", tx, id);
            if (dependents != 0)
            {
                throw new SubSshException("主机被其他主机用作跳板");
            }
            var now = Now();
            var restrictedGrant = @"SELECT DISTINCT grant_id FROM oauth_refresh_tokens WHERE all_hosts=0 AND EXISTS
                (SELECT 1 FROM json_each(oauth_refresh_tokens.host_ids_json) WHERE json_each.value=?)";
            Exec(tx, "UPDATE tokens SET expires_at=? WHERE id IN (SELECT access_token_id FROM oauth_refresh_tokens WHERE grant_id IN (" + restrictedGrant + "))", now, id);
            Exec(tx, "UPDATE oauth_refresh_tokens SET revoked_at=? WHERE grant_id IN (" + restrictedGrant + ") AND revoked_at IS NULL", now, id);
            foreach (var query in new[]
            {
                "DELETE FROM token_hosts WHERE host_id=?",
                "DELETE FROM sessions WHERE host_id=?",
                "DELETE FROM jobs WHERE host_id=?",
                "DELETE FROM metrics WHERE host_id=?",
                "DELETE FROM memories WHERE host_id=?",
            })
            {
                Exec(tx, query, id);
            }
            using (var cmd = tx.Connection!.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM hosts WHERE id=@p0";
                cmd.Parameters.AddWithValue("@p0", id);
                if (cmd.ExecuteNonQuery() == 0)
                {
                    tx.Rollback();
                    return Task.FromResult(false);
                }
            }
            tx.Commit();
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<HostConfig>> ListHostsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HostConfig>>(QueryHosts("SELECT " + HostColumns + " FROM hosts ORDER BY name"));

    public Task<HostConfig?> GetHostByNameAsync(string name, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var rd = Query("SELECT " + HostColumns + " FROM hosts WHERE name=?", name);
            return Task.FromResult(rd.Read() ? ScanHost(rd) : null);
        }
    }

    public Task<HostConfig?> GetHostAsync(long id, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var rd = Query("SELECT " + HostColumns + " FROM hosts WHERE id=?", id);
            return Task.FromResult(rd.Read() ? ScanHost(rd) : null);
        }
    }

    public Task<IReadOnlyList<string>> JumpDependentNamesAsync(long id, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var names = new List<string>();
            using var rd = Query("SELECT name FROM hosts WHERE jump_host_id=? ORDER BY name", id);
            while (rd.Read()) names.Add(rd.GetString(0));
            return Task.FromResult<IReadOnlyList<string>>(names);
        }
    }

    public Task UpdateHostFingerprintAsync(long id, string? fingerprint, CancellationToken ct = default)
    {
        lock (_sync)
        {
            Exec("UPDATE hosts SET hostkey_fp=? WHERE id=?", fingerprint, id);
        }
        return Task.CompletedTask;
    }

    /// <summary>归档主机，重复归档保留最初的归档时刻；仍被未归档主机用作跳板时拒绝。</summary>
    public Task ArchiveHostAsync(long id, long at, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var tx = _conn.BeginTransaction();
            var dependents = ScalarLong("SELECT count(*) FROM hosts WHERE jump_host_id=? AND archived_at IS NULL", tx, id);
            if (dependents != 0)
            {
                throw new SubSshException("主机被其他主机用作跳板");
            }
            ExecOne(tx, "UPDATE hosts SET archived_at=COALESCE(archived_at,?) WHERE id=?", at, id);
            tx.Commit();
        }
        return Task.CompletedTask;
    }

    /// <summary>取消归档；跳板主机仍归档时拒绝；运行中任务重新起算失联宽限期。</summary>
    public Task UnarchiveHostAsync(long id, long now, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var tx = _conn.BeginTransaction();
            var jumpArchived = ScalarLong(
                "SELECT count(*) FROM hosts h JOIN hosts j ON j.id=h.jump_host_id WHERE h.id=? AND j.archived_at IS NOT NULL", tx, id);
            if (jumpArchived != 0)
            {
                throw new SubSshException("跳板主机已归档，请先取消归档跳板主机");
            }
            ExecOne(tx, "UPDATE hosts SET archived_at=NULL WHERE id=?", id);
            Exec(tx, "UPDATE jobs SET last_checked_at=? WHERE host_id=? AND status='running'", now, id);
            tx.Commit();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HostConfig>> MonitoredHostsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HostConfig>>(QueryHosts(
            "SELECT " + HostColumns + " FROM hosts WHERE monitor_enabled=1 AND archived_at IS NULL ORDER BY name"));

    private IReadOnlyList<HostConfig> QueryHosts(string sql, params object?[] args)
    {
        lock (_sync)
        {
            var out_ = new List<HostConfig>();
            using var rd = Query(sql, args);
            while (rd.Read()) out_.Add(ScanHost(rd));
            return out_;
        }
    }

    // ---------- 底层查询辅助 ----------

    private SqliteDataReader Query(string sql, params object?[] args)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, args);
        return cmd.ExecuteReader();
    }
}
