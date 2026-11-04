// SubSsh.Plugin.Memory — 记忆引擎（对应 Go internal/memoryx/engine.go，1:1 移植）。
// 打分公式（与 Go finishRecall 完全一致）：
//   base = (dense*wVec + fts*wFTS + clamp(importance,0,1)*wImportance) / weightTotal
//     hybrid=false: wVec=0,   wFTS=0.3, wImportance=0.2（weightTotal=0.5）
//     hybrid=true : wVec=0.5, wFTS=0.3, wImportance=0.2（weightTotal=1.0）
//   recency = exp(-ln2 * ageHours / 72)          —— 72 小时半衰期
//   score = base * (0.7 + 0.3*recency) * veracityWeight(veracity)
//     veracity: stated=1, inferred=0.7, tool=0.5, 其余=0.8
//   FTS 分数 = bm25 rank 在候选集内 min/max 归一到 [0,1]（rank 越小分越高）；
//   向量分数 = clamp(cosine(query, memory), 0, 1)；LIKE 降级时 fts 固定 0.6。
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.Memory;

public sealed class MemoryEngine : IMemoryEngine, IDisposable
{
    private const double DefaultImportance = 0.5;
    private const int DefaultRecallLimit = 8;
    private const int MaxRecallLimit = 50;
    private const double RecencyHalfLife = 72.0;

    private readonly IOneSshStoreFull _store;
    private readonly ISettings _settings;
    private readonly Embedder? _embedder;

    public MemoryEngine(IOneSshStoreFull store, ISettings settings)
    {
        _store = store;
        _settings = settings;
        var cfg = new EmbeddingConfig(settings.EmbeddingApiUrl, settings.EmbeddingApiKey, settings.EmbeddingModel);
        if (cfg.Enabled())
        {
            _embedder = new Embedder(cfg);
        }
        // 否则走 Go 版 no-embedding 路径：不建 embedder，Recall 只用 FTS（或 LIKE）。
    }

    public void Dispose() => _embedder?.Dispose();

    public async Task<MemoryRememberResult> RememberAsync(RememberInput input, CancellationToken ct = default)
    {
        var content = input.Content.Trim();
        if (content == "")
            throw new SubSshException("content 不能为空");
        var importance = input.Importance;
        if (!input.ImportanceSet && importance == 0)
            importance = DefaultImportance;
        importance = Clamp(importance, 0, 1);
        var veracity = input.Veracity;
        if (veracity == "")
            veracity = "stated";
        if (!ValidVeracity(veracity))
            throw new SubSshException($"veracity 无效: {veracity}");
        var source = input.Source;
        if (source == "")
            source = "mcp";

        var existing = await _store.FindMemoryByContentAsync(input.HostId, content, ct).ConfigureAwait(false);
        if (existing != null)
        {
            // Go：同 bank 同内容去重，只升重要度、刷新 UpdatedAt。
            if (importance > existing.Importance)
                existing.Importance = importance;
            existing.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await _store.UpdateMemoryAsync(existing, ct).ConfigureAwait(false);
            return new MemoryRememberResult(existing.Id, Deduped: true, Embedded: existing.Embedding is { Length: > 0 });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var memory = new MemoryRecord
        {
            HostId = input.HostId,
            Content = content,
            Source = source,
            Importance = importance,
            Veracity = veracity,
            TokenId = input.TokenId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var embedded = false;
        if (_embedder != null)
        {
            try
            {
                var vector = await _embedder.EmbedAsync(content, ct).ConfigureAwait(false);
                memory.Embedding = VectorCodec.Encode(vector);
                memory.EmbeddingModel = _embedder.Model;
                embedded = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"memory embedding 写入失败，已退化为纯 FTS: {e.Message}");
            }
        }
        var id = await _store.AddMemoryAsync(memory, ct).ConfigureAwait(false);
        return new MemoryRememberResult(id, Deduped: false, embedded);
    }

    public async Task UpdateAsync(MemoryRecord memory, string? newContent, double? newImportance, string? newVeracity, CancellationToken ct = default)
    {
        var contentChanged = false;
        if (newContent != null)
        {
            var content = newContent.Trim();
            if (content == "")
                throw new SubSshException("content 不能为空");
            if (content != memory.Content)
            {
                memory.Content = content;
                contentChanged = true;
            }
        }
        if (newImportance != null)
            memory.Importance = Clamp(newImportance.Value, 0, 1);
        if (newVeracity != null)
        {
            if (!ValidVeracity(newVeracity))
                throw new SubSshException($"veracity 无效: {newVeracity}");
            memory.Veracity = newVeracity;
        }
        if (contentChanged)
        {
            memory.Embedding = null;
            memory.EmbeddingModel = null;
            if (_embedder != null)
            {
                try
                {
                    var vector = await _embedder.EmbedAsync(memory.Content, ct).ConfigureAwait(false);
                    memory.Embedding = VectorCodec.Encode(vector);
                    memory.EmbeddingModel = _embedder.Model;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"memory embedding 更新失败，已退化为纯 FTS: {e.Message}");
                }
            }
        }
        memory.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await _store.UpdateMemoryAsync(memory, ct).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<MemoryRecallResult> Results, string Engine)> RecallAsync(
        long? hostId, bool includeGlobal, string query, int limit, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query == "")
            throw new SubSshException("query 不能为空");
        if (limit == 0)
            limit = DefaultRecallLimit;
        limit = (int)Clamp(limit, 1, MaxRecallLimit);
        var candidateLimit = Math.Max(limit * 3, 50);
        var match = FtsMatch(query);

        if (match == "")
        {
            // FTS 无法匹配（全是短词）：LIKE 降级，fts 固定 0.6。
            var memories = await _store.SearchMemoriesLikeAsync(hostId, includeGlobal, query, candidateLimit, ct).ConfigureAwait(false);
            var likeCandidates = new Dictionary<long, RecallCandidate>();
            foreach (var memory in memories)
                likeCandidates[memory.Id] = new RecallCandidate(memory, 0.6, 0);
            return await FinishRecallAsync(likeCandidates, limit, hybrid: false, engine: "like", ct).ConfigureAwait(false);
        }

        var ftsRows = await _store.SearchMemoriesFtsAsync(hostId, includeGlobal, match, candidateLimit, ct).ConfigureAwait(false);
        var candidates = new Dictionary<long, RecallCandidate>(ftsRows.Count);
        var normalizedFts = NormalizeBm25(ftsRows);
        for (var i = 0; i < ftsRows.Count; i++)
        {
            var memory = ftsRows[i].Memory;
            candidates[memory.Id] = new RecallCandidate(memory, normalizedFts[i], 0);
        }

        var hybrid = false;
        if (_embedder != null)
        {
            float[] queryVector;
            try
            {
                queryVector = await _embedder.EmbedAsync(query, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"memory embedding 召回失败，已退化为纯 FTS: {e.Message}");
                queryVector = [];
            }
            if (queryVector.Length != 0)
            {
                hybrid = true;
                var vectors = await _store.ListMemoryVectorsAsync(hostId, includeGlobal, _embedder.Model, ct).ConfigureAwait(false);
                var dense = new List<DenseCandidate>(vectors.Count);
                foreach (var vector in vectors)
                {
                    float[] decoded;
                    try
                    {
                        decoded = VectorCodec.Decode(vector.Embedding ?? []);
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine($"memory {vector.Id} 的 embedding 无效，已跳过: {e.Message}");
                        continue;
                    }
                    dense.Add(new DenseCandidate(vector.Id, Clamp(CosineSimilarity(queryVector, decoded), 0, 1)));
                }
                dense.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Id.CompareTo(b.Id));
                if (dense.Count > candidateLimit)
                    dense.RemoveRange(candidateLimit, dense.Count - candidateLimit);
                foreach (var item in dense)
                {
                    if (!candidates.TryGetValue(item.Id, out var candidate))
                    {
                        var memory = await _store.GetMemoryAsync(item.Id, ct).ConfigureAwait(false);
                        if (memory == null)
                            continue;
                        candidate = new RecallCandidate(memory, 0, 0);
                        candidates[item.Id] = candidate;
                    }
                    candidate.Dense = item.Score;
                }
            }
        }

        return await FinishRecallAsync(candidates, limit, hybrid, hybrid ? "hybrid" : "fts", ct).ConfigureAwait(false);
    }

    private async Task<(IReadOnlyList<MemoryRecallResult> Results, string Engine)> FinishRecallAsync(
        Dictionary<long, RecallCandidate> candidates, int limit, bool hybrid, string engine, CancellationToken ct)
    {
        var wVec = hybrid ? 0.5 : 0.0;
        var wFts = 0.3;
        var wImportance = 0.2;
        var weightTotal = wVec + wFts + wImportance;
        var now = DateTimeOffset.UtcNow;
        var results = new List<MemoryRecallResult>(candidates.Count);
        foreach (var candidate in candidates.Values)
        {
            var memory = candidate.Memory;
            var baseScore = (candidate.Dense * wVec + candidate.Fts * wFts + Clamp(memory.Importance, 0, 1) * wImportance) / weightTotal;
            var ageHours = now - DateTimeOffset.FromUnixTimeSeconds(memory.CreatedAt);
            var hours = ageHours.TotalHours;
            if (hours < 0)
                hours = 0;
            var recency = Math.Exp(-Math.Log(2) * hours / RecencyHalfLife);
            var score = baseScore * (0.7 + 0.3 * recency) * VeracityWeight(memory.Veracity);
            results.Add(new MemoryRecallResult
            {
                Id = memory.Id,
                Bank = memory.HostId == null ? "global" : "",
                HostId = memory.HostId,
                Content = memory.Content,
                Source = memory.Source,
                Importance = memory.Importance,
                Veracity = memory.Veracity,
                Score = Round4(score),
                FtsScore = Round4(candidate.Fts),
                DenseScore = Round4(candidate.Dense),
                CreatedAt = memory.CreatedAt,
                RecallCount = memory.RecallCount + 1,
            });
        }
        results.Sort((a, b) =>
        {
            if (a.Score != b.Score) return b.Score.CompareTo(a.Score);
            if (a.CreatedAt != b.CreatedAt) return b.CreatedAt.CompareTo(a.CreatedAt);
            return a.Id.CompareTo(b.Id);
        });
        if (results.Count > limit)
            results.RemoveRange(limit, results.Count - limit);
        var ids = new long[results.Count];
        for (var i = 0; i < results.Count; i++)
            ids[i] = results[i].Id;
        await _store.TouchMemoryRecallsAsync(ids, now.ToUnixTimeSeconds(), ct).ConfigureAwait(false);
        return (results, engine);
    }

    public async Task<MemorySleepReport> SleepAsync(long? hostId, CancellationToken ct = default)
    {
        var report = new MemorySleepReport();
        report = report with { Deduped = await _store.DedupeMemoriesAsync(hostId, ct).ConfigureAwait(false) };
        var now = DateTimeOffset.UtcNow;
        report = report with
        {
            Decayed = await _store.DecayMemoriesAsync(hostId, now.AddDays(-30).ToUnixTimeSeconds(), ct).ConfigureAwait(false),
        };
        report = report with
        {
            Pruned = await _store.PruneMemoriesAsync(hostId, now.AddDays(-90).ToUnixTimeSeconds(), ct).ConfigureAwait(false),
        };
        return report;
    }

    // ---- Go 引擎内部函数（照抄） ----

    /// <summary>FTS5 MATCH 串：长度 &gt;= 3 的词加双引号短语，OR 连接。</summary>
    private static string FtsMatch(string query)
    {
        var tokens = Fields(query);
        var phrases = new List<string>(tokens.Count);
        foreach (var token in tokens)
        {
            if (token.EnumerateRunes().Count() < 3) // Go utf8.RuneCountInString(token) < 3：按 rune 数计
                continue;
            phrases.Add("\"" + token.Replace("\"", "\"\"") + "\"");
        }
        return string.Join(" OR ", phrases);
    }

    /// <summary>Go strings.Fields：按 Unicode 空白切分。</summary>
    private static List<string> Fields(string s)
    {
        var result = new List<string>();
        var start = -1;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                if (start >= 0) { result.Add(s[start..i]); start = -1; }
            }
            else if (start < 0)
            {
                start = i;
            }
        }
        if (start >= 0) result.Add(s[start..]);
        return result;
    }

    /// <summary>bm25 rank 在候选集内归一到 [0,1]（rank 越小相关性越高）。</summary>
    private static double[] NormalizeBm25(IReadOnlyList<MemoryWithRank> rows)
    {
        if (rows.Count == 0)
            return [];
        double minRank = rows[0].Rank, maxRank = rows[0].Rank;
        foreach (var row in rows)
        {
            minRank = Math.Min(minRank, row.Rank);
            maxRank = Math.Max(maxRank, row.Rank);
        }
        var scores = new double[rows.Count];
        if (minRank == maxRank)
        {
            Array.Fill(scores, 1);
            return scores;
        }
        for (var i = 0; i < rows.Count; i++)
            scores[i] = Clamp((maxRank - rows[i].Rank) / (maxRank - minRank), 0, 1);
        return scores;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length)
            return 0;
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var left = (double)a[i];
            var right = (double)b[i];
            dot += left * right;
            normA += left * left;
            normB += right * right;
        }
        if (normA == 0 || normB == 0)
            return 0;
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private static bool ValidVeracity(string value) => value is "stated" or "inferred" or "tool" or "unknown";

    private static double VeracityWeight(string value) => value switch
    {
        "stated" => 1,
        "inferred" => 0.7,
        "tool" => 0.5,
        _ => 0.8,
    };

    private static double Clamp(double value, double low, double high) => Math.Min(high, Math.Max(low, value));

    private static double Round4(double value) => Math.Round(value * 10000) / 10000;

    private sealed class RecallCandidate(MemoryRecord memory, double fts, double dense)
    {
        public MemoryRecord Memory { get; } = memory;
        public double Fts { get; set; } = fts;
        public double Dense { get; set; } = dense;
    }

    private readonly record struct DenseCandidate(long Id, double Score);
}
