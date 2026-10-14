using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BelgeRag.Core.Chat;
using BelgeRag.Core.Embeddings;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace BelgeRag.Infrastructure.Redis;

/// <summary>Süreç ömrü boyunca önbellek isabet sayaçları (Singleton, kilitsiz).</summary>
public sealed class CacheStats
{
    private long _embHits, _embMisses, _ansHits, _ansMisses, _errors;

    public void EmbeddingHits(int n) => Interlocked.Add(ref _embHits, n);
    public void EmbeddingMisses(int n) => Interlocked.Add(ref _embMisses, n);
    public void AnswerHit() => Interlocked.Increment(ref _ansHits);
    public void AnswerMiss() => Interlocked.Increment(ref _ansMisses);
    public void Error() => Interlocked.Increment(ref _errors);

    public object Snapshot() => new
    {
        embeddingHits = Interlocked.Read(ref _embHits),
        embeddingMisses = Interlocked.Read(ref _embMisses),
        answerHits = Interlocked.Read(ref _ansHits),
        answerMisses = Interlocked.Read(ref _ansMisses),
        redisErrors = Interlocked.Read(ref _errors),
    };
}

/// <summary>
/// <see cref="IEmbeddingService"/> dekoratörü: anahtar = model + SHA-256(metin), değer = ham float32 baytları.
/// Toplu isteklerde MGET ile tek gidiş-dönüş; yalnızca eksik olanlar modele gider.
/// Redis erişilemezse önbelleği atlayıp doğrudan modele düşer (önbellek bir optimizasyondur, bağımlılık değil).
/// </summary>
public sealed class CachedEmbeddingService(
    IEmbeddingService inner,
    IConnectionMultiplexer redis,
    RedisOptions options,
    CacheStats stats,
    ILogger<CachedEmbeddingService> logger) : IEmbeddingService
{
    public string ModelId => inner.ModelId;
    public int Dimensions => inner.Dimensions;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct = default)
    {
        var keys = texts.Select(t => (RedisKey)Key(inner.ModelId, purpose, t)).ToArray();
        RedisValue[] cached;
        try
        {
            cached = await redis.GetDatabase().StringGetAsync(keys);
        }
        catch (RedisException e)
        {
            stats.Error();
            logger.LogWarning(e, "Embedding önbelleği okunamadı; doğrudan modele gidiliyor");
            return await inner.EmbedAsync(texts, purpose, ct);
        }

        var result = new float[texts.Count][];
        var missing = new List<int>();
        for (var i = 0; i < texts.Count; i++)
        {
            if (cached[i].HasValue) result[i] = MemoryMarshal.Cast<byte, float>((byte[])cached[i]!).ToArray();
            else missing.Add(i);
        }
        stats.EmbeddingHits(texts.Count - missing.Count);
        stats.EmbeddingMisses(missing.Count);
        if (missing.Count == 0) return result;

        var fresh = await inner.EmbedAsync(missing.Select(i => texts[i]).ToList(), purpose, ct);
        var batch = redis.GetDatabase().CreateBatch();
        var writes = new List<Task>(missing.Count);
        for (var j = 0; j < missing.Count; j++)
        {
            result[missing[j]] = fresh[j];
            writes.Add(batch.StringSetAsync(keys[missing[j]], MemoryMarshal.AsBytes(fresh[j].AsSpan()).ToArray(), options.EmbeddingTtl));
        }
        batch.Execute();
        try
        {
            await Task.WhenAll(writes);
        }
        catch (RedisException e)
        {
            stats.Error();
            logger.LogWarning(e, "Embedding önbelleğine yazılamadı");
        }
        return result;
    }

    public static string Key(string model, EmbeddingPurpose purpose, string text) =>
        $"emb:{model}:{(purpose == EmbeddingPurpose.Query ? "q" : "d")}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()}";
}

public sealed class RedisAnswerCache(
    IConnectionMultiplexer redis,
    RedisOptions options,
    CacheStats stats,
    ILogger<RedisAnswerCache> logger) : IAnswerCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CachedAnswer?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key);
            if (value.IsNullOrEmpty)
            {
                stats.AnswerMiss();
                return null;
            }
            stats.AnswerHit();
            return JsonSerializer.Deserialize<CachedAnswer>((string)value!, Json);
        }
        catch (RedisException e)
        {
            stats.Error();
            logger.LogWarning(e, "Yanıt önbelleği okunamadı");
            return null;
        }
    }

    public async Task SetAsync(string key, CachedAnswer answer, CancellationToken ct)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(answer, Json), options.AnswerTtl);
        }
        catch (RedisException e)
        {
            stats.Error();
            logger.LogWarning(e, "Yanıt önbelleğine yazılamadı");
        }
    }
}
