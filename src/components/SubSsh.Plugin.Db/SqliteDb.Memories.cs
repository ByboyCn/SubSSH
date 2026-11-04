// SubSsh.Plugin.Db — memories（对应 Go internal/store/memories.go）。
using Microsoft.Data.Sqlite;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.Db;

public sealed partial class SqliteDb
{
    private const string MemoryColumns =
        "id,host_id,content,source,importance,veracity,token_id,created_at,updated_at,recall_count,last_recalled,embedding,embedding_model";

    private static MemoryRecord ScanMemory(SqliteDataReader rd)
    {
        var m = new MemoryRecord
        {
            Id = IL(rd, 0),
            HostId = NL(rd, 1),
            Content = SN(rd, 2),
            Source = SN(rd, 3),
            Importance = rd.GetDouble(4),
            Veracity = SN(rd, 5),
            TokenId = NL(rd, 6),
            CreatedAt = IL(rd, 7),
            UpdatedAt = IL(rd, 8),
            RecallCount = IL(rd, 9),
            LastRecalled = NL(rd, 10),
            Embedding = BL(rd, 11),
            EmbeddingModel = ST(rd, 12),
        };
        return m;
    }

    private static MemoryWithRank ScanMemoryWithRank(SqliteDataReader rd)
    {
        var m = new MemoryWithRank
        {
            Memory = new MemoryRecord
            {
                Id = IL(rd, 0),
                HostId = NL(rd, 1),
                Content = SN(rd, 2),
                Source = SN(rd, 3),
                Importance = rd.GetDouble(4),
                Veracity = SN(rd, 5),
                TokenId = NL(rd, 6),
                CreatedAt = IL(rd, 7),
                UpdatedAt = IL(rd, 8),
                RecallCount = IL(rd, 9),
                LastRecalled = NL(rd, 10),
                Embedding = BL(rd, 11),
                EmbeddingModel = ST(rd, 12),
            },
            Rank = rd.GetDouble(13),
        };
        return m;
    }

    private static string PrefixedMemoryColumns(string prefix)
        => string.Join(",", MemoryColumns.Split(',').Select(c => prefix + "." + c));

    /// <summary>hostID 为 null 时限定全局库；includeGlobal 时同时包含该主机与全局库。</summary>
    private static (string Scope, List<object?> Args) MemoryScope(long? hostId, bool includeGlobal)
    {
        if (hostId == null) return ("m.host_id IS NULL", []);
        if (includeGlobal) return ("(m.host_id=? OR m.host_id IS NULL)", [hostId]);
        return ("m.host_id=?", [hostId]);
    }

    public Task<long> AddMemoryAsync(MemoryRecord m, CancellationToken ct = default)
    {
        lock (_sync)
        {
            Exec(@"INSERT INTO memories(host_id,content,source,importance,veracity,token_id,created_at,updated_at,recall_count,last_recalled,embedding,embedding_model)
                VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
                m.HostId, m.Content, m.Source, m.Importance, m.Veracity, m.TokenId, m.CreatedAt, m.UpdatedAt,
                m.RecallCount, m.LastRecalled, m.Embedding, m.EmbeddingModel);
            return Task.FromResult(ScalarLong("SELECT last_insert_rowid()"));
        }
    }

    public Task<MemoryRecord?> FindMemoryByContentAsync(long? hostId, string content, CancellationToken ct = default)
    {
        lock (_sync)
        {
            // SQL 中 host_id=? 出现两次，? 按序取值，需传两次 hostId（与 Go 一致）。
            using var rd = Query(@"SELECT " + MemoryColumns + @" FROM memories
                WHERE content=? AND (host_id=? OR (? IS NULL AND host_id IS NULL)) LIMIT 1", content, hostId, hostId);
            return Task.FromResult(rd.Read() ? ScanMemory(rd) : null);
        }
    }

    public Task<MemoryRecord?> GetMemoryAsync(long id, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var rd = Query("SELECT " + MemoryColumns + " FROM memories WHERE id=?", id);
            return Task.FromResult(rd.Read() ? ScanMemory(rd) : null);
        }
    }

    public Task UpdateMemoryAsync(MemoryRecord m, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE memories SET content=?,importance=?,veracity=?,updated_at=?,embedding=?,embedding_model=? WHERE id=?";
            Bind(cmd, [m.Content, m.Importance, m.Veracity, m.UpdatedAt, m.Embedding, m.EmbeddingModel, m.Id]);
            if (cmd.ExecuteNonQuery() == 0)
            {
                throw new SubSshException("记录不存在");
            }
        }
        return Task.CompletedTask;
    }

    public Task DeleteMemoryAsync(long id, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM memories WHERE id=?";
            cmd.Parameters.AddWithValue("@p0", id);
            if (cmd.ExecuteNonQuery() == 0)
            {
                throw new SubSshException("记录不存在");
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemoryRecord>> ListMemoriesAsync(long? hostId, int limit, int offset, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var out_ = new List<MemoryRecord>();
            using var rd = Query(@"SELECT " + MemoryColumns + @" FROM memories
                WHERE host_id=? OR (? IS NULL AND host_id IS NULL)
                ORDER BY created_at DESC,id DESC LIMIT ? OFFSET ?", hostId, hostId, limit, offset);
            while (rd.Read()) out_.Add(ScanMemory(rd));
            return Task.FromResult<IReadOnlyList<MemoryRecord>>(out_);
        }
    }

    public Task<IReadOnlyList<MemoryAdminRow>> ListMemoriesAdminAsync(long? hostId, string query, int limit, int offset, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var sql = @"SELECT m.id,m.host_id,h.name,m.content,m.source,m.importance,m.veracity,m.created_at,m.updated_at,m.recall_count,m.last_recalled
                FROM memories m LEFT JOIN hosts h ON h.id=m.host_id WHERE 1=1";
            var args = new List<object?>(4);
            if (hostId != null)
            {
                if (hostId == 0)
                {
                    sql += " AND m.host_id IS NULL";
                }
                else
                {
                    sql += " AND m.host_id=?";
                    args.Add(hostId);
                }
            }
            if (query.Length > 0)
            {
                sql += " AND m.content LIKE ? ESCAPE '\\'";
                args.Add("%" + EscapeLike(query) + "%");
            }
            sql += " ORDER BY m.created_at DESC,m.id DESC LIMIT ? OFFSET ?";
            args.Add(limit);
            args.Add(offset);
            var out_ = new List<MemoryAdminRow>();
            using var rd = Query(sql, args.ToArray());
            while (rd.Read())
            {
                out_.Add(new MemoryAdminRow
                {
                    Id = IL(rd, 0),
                    HostId = NL(rd, 1),
                    HostName = ST(rd, 2),
                    Content = SN(rd, 3),
                    Source = SN(rd, 4),
                    Importance = rd.GetDouble(5),
                    Veracity = SN(rd, 6),
                    CreatedAt = IL(rd, 7),
                    UpdatedAt = IL(rd, 8),
                    RecallCount = IL(rd, 9),
                    LastRecalled = NL(rd, 10),
                });
            }
            return Task.FromResult<IReadOnlyList<MemoryAdminRow>>(out_);
        }
    }

    public Task<IReadOnlyList<MemoryWithRank>> SearchMemoriesFtsAsync(long? hostId, bool includeGlobal, string match, int limit, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var (scope, args) = MemoryScope(hostId, includeGlobal);
            var sql = "SELECT " + PrefixedMemoryColumns("m") + ",bm25(memories_fts)" +
                " FROM memories_fts JOIN memories m ON m.id=memories_fts.rowid" +
                " WHERE memories_fts MATCH ? AND " + scope + " ORDER BY bm25(memories_fts) LIMIT ?";
            args.Insert(0, match);
            args.Add(limit);
            var out_ = new List<MemoryWithRank>();
            using var rd = Query(sql, args.ToArray());
            while (rd.Read()) out_.Add(ScanMemoryWithRank(rd));
            return Task.FromResult<IReadOnlyList<MemoryWithRank>>(out_);
        }
    }

    public Task<IReadOnlyList<MemoryRecord>> SearchMemoriesLikeAsync(long? hostId, bool includeGlobal, string query, int limit, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var (scope, args) = MemoryScope(hostId, includeGlobal);
            var sql = "SELECT " + PrefixedMemoryColumns("m") + " FROM memories m WHERE " + scope +
                " AND m.content LIKE ? ESCAPE '\\'" +
                " ORDER BY m.created_at DESC,m.id DESC LIMIT ?";
            args.Add("%" + EscapeLike(query) + "%");
            args.Add(limit);
            var out_ = new List<MemoryRecord>();
            using var rd = Query(sql, args.ToArray());
            while (rd.Read()) out_.Add(ScanMemory(rd));
            return Task.FromResult<IReadOnlyList<MemoryRecord>>(out_);
        }
    }

    public Task<IReadOnlyList<MemoryVector>> ListMemoryVectorsAsync(long? hostId, bool includeGlobal, string model, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var (scope, args) = MemoryScope(hostId, includeGlobal);
            var sql = "SELECT m.id,m.embedding FROM memories m WHERE " + scope +
                " AND m.embedding IS NOT NULL AND m.embedding_model=?";
            args.Add(model);
            var out_ = new List<MemoryVector>();
            using var rd = Query(sql, args.ToArray());
            while (rd.Read())
            {
                out_.Add(new MemoryVector { Id = IL(rd, 0), Embedding = BL(rd, 1) });
            }
            return Task.FromResult<IReadOnlyList<MemoryVector>>(out_);
        }
    }

    public Task TouchMemoryRecallsAsync(IReadOnlyList<long> ids, long now, CancellationToken ct = default)
    {
        if (ids.Count == 0) return Task.CompletedTask;
        lock (_sync)
        {
            var args = new List<object?>(ids.Count + 1) { now };
            args.AddRange(ids);
            Exec("UPDATE memories SET recall_count=recall_count+1,last_recalled=? WHERE id IN (" + Placeholders(ids.Count) + ")", args.ToArray());
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemoryBankStat>> MemoryStatsAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            var out_ = new List<MemoryBankStat>();
            using var rd = Query(@"SELECT host_id,count(*),sum(CASE WHEN embedding IS NOT NULL THEN 1 ELSE 0 END),max(created_at)
                FROM memories GROUP BY host_id ORDER BY host_id");
            while (rd.Read())
            {
                var embedded = rd.IsDBNull(2) ? 0L : rd.GetInt64(2);
                out_.Add(new MemoryBankStat
                {
                    HostId = NL(rd, 0),
                    Count = IL(rd, 1),
                    Embedded = embedded,
                    LastWritten = NL(rd, 3),
                });
            }
            return Task.FromResult<IReadOnlyList<MemoryBankStat>>(out_);
        }
    }

    /// <summary>合并相同正文，返回删除条数。</summary>
    public Task<long> DedupeMemoriesAsync(long? hostId, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var tx = _conn.BeginTransaction();
            var contents = new List<string>();
            using (var rd = Query(@"SELECT content FROM memories WHERE host_id=? OR (? IS NULL AND host_id IS NULL)
                GROUP BY content HAVING count(*)>1", hostId, hostId))
            {
                while (rd.Read()) contents.Add(rd.GetString(0));
            }
            long deleted = 0;
            foreach (var content in contents)
            {
                long keeper, totalRecalls;
                using (var rd = Query(@"SELECT id,(SELECT sum(recall_count) FROM memories WHERE content=? AND (host_id=? OR (? IS NULL AND host_id IS NULL)))
                    FROM memories WHERE content=? AND (host_id=? OR (? IS NULL AND host_id IS NULL))
                    ORDER BY importance DESC,created_at ASC,id ASC LIMIT 1", content, hostId, hostId, content, hostId, hostId))
                {
                    rd.Read();
                    keeper = rd.GetInt64(0);
                    totalRecalls = rd.IsDBNull(1) ? 0 : rd.GetInt64(1);
                }
                Exec(tx, "UPDATE memories SET recall_count=? WHERE id=?", totalRecalls, keeper);
                using (var cmd = tx.Connection!.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"DELETE FROM memories WHERE content=? AND id<>? AND (host_id=? OR (? IS NULL AND host_id IS NULL))";
                    Bind(cmd, [content, keeper, hostId, hostId]);
                    deleted += cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
            return Task.FromResult(deleted);
        }
    }

    /// <summary>重要度衰减，返回处理条数。</summary>
    public Task<long> DecayMemoriesAsync(long? hostId, long cutoff, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"UPDATE memories SET importance=max(0.05,importance*0.9)
                WHERE (host_id=? OR (? IS NULL AND host_id IS NULL)) AND max(created_at,coalesce(last_recalled,0))<? AND importance>0.05";
            Bind(cmd, [hostId, hostId, cutoff]);
            return Task.FromResult((long)cmd.ExecuteNonQuery());
        }
    }

    /// <summary>删除长期低重要度未召回记忆，返回删除条数。</summary>
    public Task<long> PruneMemoriesAsync(long? hostId, long cutoff, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"DELETE FROM memories WHERE (host_id=? OR (? IS NULL AND host_id IS NULL))
                AND importance<=0.1 AND recall_count=0 AND created_at<?";
            Bind(cmd, [hostId, hostId, cutoff]);
            return Task.FromResult((long)cmd.ExecuteNonQuery());
        }
    }
}
