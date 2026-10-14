using BelgeRag.Core.Text;

namespace BelgeRag.Core.Embeddings;

/// <summary>
/// Bazı embedding modelleri (ör. nomic-embed-text, E5) sorgu ve belge için farklı görev önekleri bekler;
/// asimetrik arama bu ayrıma dayanır.
/// </summary>
public enum EmbeddingPurpose
{
    Document,
    Query,
}

public interface IEmbeddingService
{
    /// <summary>Önbellek anahtarlarında ve chunk satırlarında saklanan model kimliği.</summary>
    string ModelId { get; }

    int Dimensions { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct = default);
}

public static class EmbeddingServiceExtensions
{
    public static async Task<float[]> EmbedQueryAsync(this IEmbeddingService svc, string text, CancellationToken ct = default) =>
        (await svc.EmbedAsync([text], EmbeddingPurpose.Query, ct))[0];
}

/// <summary>
/// Model gerektirmeyen, deterministik "feature hashing" embedding'i: sözcük terimleri ve
/// 3–5 karakterlik n-gram'lar işaretli FNV-1a ile sabit boyutlu bir vektöre katlanır,
/// alt-doğrusal tf ile ağırlıklandırılır ve L2 normalize edilir. Anlamsal benzerlik
/// yakalamaz (eş anlamlılar uzak düşer) ama yazım/ek farklılıklarına dayanıklıdır.
/// LLM kredisi veya GPU olmadan tüm hattın uçtan uca çalışması ve testlerin
/// tekrarlanabilir olması için var. string.GetHashCode süreç başına rastgele tohumlandığı
/// için kullanılmaz; aksi halde API ve worker farklı vektör üretirdi.
/// </summary>
public sealed class HashingEmbeddingService(int dimensions = 384) : IEmbeddingService
{
    public string ModelId => $"hashing-ngram-{Dimensions}";
    public int Dimensions { get; } = dimensions;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct = default)
    {
        IReadOnlyList<float[]> result = texts.Select(Embed).ToArray();
        return Task.FromResult(result);
    }

    internal float[] Embed(string text)
    {
        var counts = new Dictionary<int, float>();
        void Add(string feature, float weight)
        {
            var h = Fnv1a(feature);
            var idx = (int)(h % (uint)Dimensions);
            var sign = (h >> 31) == 0 ? 1f : -1f;
            counts[idx] = counts.GetValueOrDefault(idx) + sign * weight;
        }

        foreach (var term in TextFolding.ContentTerms(text))
        {
            Add("w:" + term, 1.0f);
            var padded = $"<{term}>";
            for (var n = 3; n <= 5; n++)
                for (var i = 0; i + n <= padded.Length; i++)
                    Add("c:" + padded.Substring(i, n), 0.35f);
        }

        var v = new float[Dimensions];
        foreach (var (idx, value) in counts)
            v[idx] = MathF.Sign(value) * MathF.Log(1 + MathF.Abs(value));
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0)
            for (var i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }

    private static uint Fnv1a(string s)
    {
        var hash = 2166136261u;
        foreach (var ch in s)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return hash;
    }
}
