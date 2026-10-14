using System.Diagnostics;
using BelgeRag.Core.Embeddings;
using BelgeRag.Core.Text;

namespace BelgeRag.Core.Search;

public enum SearchMode
{
    Hybrid,
    Vector,
    FullText,
}

public sealed record SearchHit(
    Guid ChunkId,
    Guid DocumentId,
    string DocumentTitle,
    int Ordinal,
    string Text,
    double Score)
{
    public int? VectorRank { get; init; }
    public int? TextRank { get; init; }
}

public sealed record SearchRequest(string Query, int TopK = 5, SearchMode Mode = SearchMode.Hybrid, bool Rerank = true);

public sealed record SearchTimings(double EmbedMs, double VectorMs, double FullTextMs, double RerankMs, double TotalMs);

public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, SearchTimings Timings);

/// <summary>Ham arama dizini (PostgreSQL + pgvector uygulaması Infrastructure'da).</summary>
public interface ISearchIndex
{
    Task<IReadOnlyList<SearchHit>> VectorSearchAsync(float[] queryEmbedding, string embeddingModel, int limit, CancellationToken ct);

    Task<IReadOnlyList<SearchHit>> FullTextSearchAsync(string query, int limit, CancellationToken ct);
}

public static class ReciprocalRankFusion
{
    /// <summary>
    /// RRF: skor(d) = Σ 1 / (k + rank_i(d)). Vektör ve tam metin skorları farklı ölçeklerde
    /// (kosinüs vs ts_rank) olduğu için doğrudan toplanamaz; RRF yalnızca sıralara bakar.
    /// k=60, Cormack vd. (2009) tarafından önerilen değerdir.
    /// </summary>
    public static IReadOnlyList<SearchHit> Fuse(IReadOnlyList<SearchHit> vector, IReadOnlyList<SearchHit> fullText, int k = 60)
    {
        var fused = new Dictionary<Guid, (SearchHit Hit, double Score, int? VRank, int? TRank)>();
        for (var i = 0; i < vector.Count; i++)
        {
            var h = vector[i];
            fused[h.ChunkId] = (h, 1.0 / (k + i + 1), i + 1, null);
        }
        for (var i = 0; i < fullText.Count; i++)
        {
            var h = fullText[i];
            var add = 1.0 / (k + i + 1);
            fused[h.ChunkId] = fused.TryGetValue(h.ChunkId, out var e)
                ? (e.Hit, e.Score + add, e.VRank, i + 1)
                : (h, add, null, i + 1);
        }
        return fused.Values
            .OrderByDescending(e => e.Score)
            .ThenBy(e => Math.Min(e.VRank ?? int.MaxValue, e.TRank ?? int.MaxValue))
            .Select(e => e.Hit with { Score = e.Score, VectorRank = e.VRank, TextRank = e.TRank })
            .ToList();
    }
}

public interface IReranker
{
    string Name { get; }

    Task<IReadOnlyList<SearchHit>> RerankAsync(string query, IReadOnlyList<SearchHit> candidates, CancellationToken ct);
}

/// <summary>
/// Aday kümesi üzerinde BM25 + terim kapsama + yakınlık skoruyla yeniden sıralama.
/// Cross-encoder modeli gerektirmez. IDF yalnızca aday kümesinden hesaplanır (yerel IDF):
/// soru terimlerinden adayların çoğunda geçenler ayırt edici değildir.
/// Son skor, ilk aşama sırasını da korumak için RRF sırasıyla harmanlanır.
/// </summary>
public sealed class LexicalReranker : IReranker
{
    public string Name => "lexical-bm25";

    public Task<IReadOnlyList<SearchHit>> RerankAsync(string query, IReadOnlyList<SearchHit> candidates, CancellationToken ct)
    {
        var qTerms = TextFolding.ContentTerms(query).Distinct().ToArray();
        if (qTerms.Length == 0 || candidates.Count < 2) return Task.FromResult(candidates);

        var docs = candidates.Select(c => TextFolding.ContentTerms(c.Text).ToArray()).ToArray();
        var avgLen = docs.Average(d => (double)Math.Max(d.Length, 1));
        var df = qTerms.ToDictionary(t => t, t => docs.Count(d => d.Contains(t)));
        const double k1 = 1.2, b = 0.75;

        var scored = new List<(SearchHit Hit, double Score)>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var d = docs[i];
            double bm25 = 0;
            var covered = 0;
            foreach (var t in qTerms)
            {
                var tf = d.Count(x => x == t);
                if (tf == 0) continue;
                covered++;
                var idf = Math.Log(1 + (candidates.Count - df[t] + 0.5) / (df[t] + 0.5));
                bm25 += idf * tf * (k1 + 1) / (tf + k1 * (1 - b + b * d.Length / avgLen));
            }
            var coverage = (double)covered / qTerms.Length;
            var proximity = Proximity(d, qTerms);
            var firstStage = 1.0 / (i + 1);
            scored.Add((candidates[i], bm25 + 2.0 * coverage + proximity + 0.5 * firstStage));
        }

        IReadOnlyList<SearchHit> result = scored
            .OrderByDescending(s => s.Score)
            .Select(s => s.Hit with { Score = Math.Round(s.Score, 4) })
            .ToList();
        return Task.FromResult(result);
    }

    /// <summary>Farklı soru terimleri 8 terimlik bir pencerede birlikte geçiyorsa bonus.</summary>
    private static double Proximity(string[] doc, string[] qTerms)
    {
        if (qTerms.Length < 2) return 0;
        var best = 0;
        for (var i = 0; i < doc.Length; i++)
        {
            var window = doc.Skip(i).Take(8);
            var distinct = qTerms.Count(t => window.Contains(t));
            if (distinct > best) best = distinct;
        }
        return best >= 2 ? (double)best / qTerms.Length : 0;
    }
}

public sealed class NoopReranker : IReranker
{
    public string Name => "none";

    public Task<IReadOnlyList<SearchHit>> RerankAsync(string query, IReadOnlyList<SearchHit> candidates, CancellationToken ct) =>
        Task.FromResult(candidates);
}

public sealed record HybridSearchOptions
{
    /// <summary>Her bir ilk aşama aramasından (vektör, tam metin) alınacak aday sayısı.</summary>
    public int CandidatesPerRetriever { get; init; } = 20;

    /// <summary>Reranker'a verilecek en fazla birleşik aday.</summary>
    public int RerankWindow { get; init; } = 20;

    public int RrfK { get; init; } = 60;
}

/// <summary>
/// Sorgu embedding'i → (vektör ∥ tam metin, paralel) → RRF → rerank → top-k.
/// Scoped: kullandığı dizin (DB bağlantısı) istek kapsamlıdır.
/// </summary>
public sealed class HybridSearchService(
    ISearchIndex index,
    IEmbeddingService embeddings,
    IReranker reranker,
    HybridSearchOptions options)
{
    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        double embedMs = 0, vectorMs = 0, textMs = 0, rerankMs = 0;
        var limit = Math.Max(options.CandidatesPerRetriever, request.TopK);

        Task<(IReadOnlyList<SearchHit>, double)> vectorTask = Task.FromResult<(IReadOnlyList<SearchHit>, double)>(([], 0));
        Task<(IReadOnlyList<SearchHit>, double)> textTask = Task.FromResult<(IReadOnlyList<SearchHit>, double)>(([], 0));

        if (request.Mode is SearchMode.Hybrid or SearchMode.Vector)
        {
            var sw = Stopwatch.StartNew();
            var qv = await embeddings.EmbedQueryAsync(request.Query, ct);
            embedMs = sw.Elapsed.TotalMilliseconds;
            vectorTask = Timed(() => index.VectorSearchAsync(qv, embeddings.ModelId, limit, ct));
        }
        if (request.Mode is SearchMode.Hybrid or SearchMode.FullText)
            textTask = Timed(() => index.FullTextSearchAsync(request.Query, limit, ct));

        var (vector, vMs) = await vectorTask;
        var (text, tMs) = await textTask;
        vectorMs = vMs;
        textMs = tMs;

        IReadOnlyList<SearchHit> merged = request.Mode switch
        {
            SearchMode.Vector => vector.Select((h, i) => h with { VectorRank = i + 1 }).ToList(),
            SearchMode.FullText => text.Select((h, i) => h with { TextRank = i + 1 }).ToList(),
            _ => ReciprocalRankFusion.Fuse(vector, text, options.RrfK),
        };

        if (request.Rerank && merged.Count > 1)
        {
            var sw = Stopwatch.StartNew();
            var window = merged.Take(options.RerankWindow).ToList();
            var reranked = await reranker.RerankAsync(request.Query, window, ct);
            merged = reranked.Concat(merged.Skip(options.RerankWindow)).ToList();
            rerankMs = sw.Elapsed.TotalMilliseconds;
        }

        var hits = merged.Take(request.TopK).ToList();
        return new SearchResult(hits, new SearchTimings(
            Math.Round(embedMs, 2), Math.Round(vectorMs, 2), Math.Round(textMs, 2), Math.Round(rerankMs, 2),
            Math.Round(total.Elapsed.TotalMilliseconds, 2)));
    }

    private static async Task<(IReadOnlyList<SearchHit>, double)> Timed(Func<Task<IReadOnlyList<SearchHit>>> run)
    {
        var sw = Stopwatch.StartNew();
        var r = await run();
        return (r, sw.Elapsed.TotalMilliseconds);
    }
}
