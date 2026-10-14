using BelgeRag.Core.Chat;
using BelgeRag.Core.Embeddings;
using BelgeRag.Core.Ingestion;
using BelgeRag.Core.Search;
using BelgeRag.Infrastructure.Mongo;
using BelgeRag.Infrastructure.Postgres;
using BelgeRag.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace BelgeRag.IntegrationTests;

[Collection(InfraCollection.Name)]
public sealed class PostgresSearchTests(InfraFixture infra) : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync() => _host = await infra.StartInfraHostAsync();

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        await ((IAsyncDisposable)_host).DisposeAsync();
    }

    private async Task<Guid> IndexAsync(string title, string content)
    {
        using var scope = _host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
        var id = await repo.CreateAsync(title, content, "test", default);
        await scope.ServiceProvider.GetRequiredService<DocumentIngestionService>().IngestAsync(id, default);
        return id;
    }

    [Fact]
    public async Task VectorAndFullText_FindIndexedChunk_WithTurkishSuffixes()
    {
        var unique = $"zirkonyum{Guid.NewGuid():N}"[..14];
        await IndexAsync("seramik", $"Kaplamada {unique} oksit katmanı termal bariyer olarak kullanılır. Katmanlar plazma püskürtmeyle uygulanır.");

        using var scope = _host.Services.CreateScope();
        var index = scope.ServiceProvider.GetRequiredService<ISearchIndex>();
        var emb = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

        // Sorgu "katmanları" (ekli), belge "katmanı"/"Katmanlar": önek eşleşmesi (katman:*) yakalamalı.
        var text = await index.FullTextSearchAsync($"{unique} katmanları", 5, default);
        Assert.Contains(text, h => h.Text.Contains(unique));

        var vector = await index.VectorSearchAsync(await emb.EmbedQueryAsync($"{unique} oksit"), emb.ModelId, 3, default);
        Assert.Contains(vector, h => h.Text.Contains(unique));
        Assert.All(vector, h => Assert.InRange(h.Score, -1.0001, 1.0001));
    }

    [Theory]
    [InlineData("a & b | !c")]
    [InlineData("'); DROP TABLE chunks; --")]
    [InlineData(":* () <-> \\")]
    public async Task FullTextQuery_IsSafeAgainstTsQuerySyntaxAndInjection(string hostile)
    {
        using var scope = _host.Services.CreateScope();
        var index = scope.ServiceProvider.GetRequiredService<ISearchIndex>();
        var ex = await Record.ExceptionAsync(() => index.FullTextSearchAsync(hostile, 5, default));
        Assert.Null(ex);

        await using var cmd = _host.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand("SELECT to_regclass('chunks') IS NOT NULL");
        Assert.True((bool)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Reingest_ReplacesChunks_InsteadOfDuplicating()
    {
        var id = await IndexAsync("tekrar", string.Join("\n\n", Enumerable.Range(0, 12).Select(i => $"Paragraf {i} içerik cümlesi burada. İkinci cümle {i}.")));
        using var scope = _host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DocumentIngestionService>().IngestAsync(id, default);

        await using var cmd = _host.Services.GetRequiredService<NpgsqlDataSource>()
            .CreateCommand("SELECT count(*), (SELECT chunk_count FROM documents WHERE id = $1) FROM chunks WHERE document_id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        Assert.Equal(r.GetInt64(0), r.GetInt32(1));
    }

    [Fact]
    public async Task Schema_RejectsDimensionMismatch()
    {
        var schema = new PgSchema(_host.Services.GetRequiredService<NpgsqlDataSource>(), InfraFixture.Dimensions + 1);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => schema.EnsureAsync(default));
        Assert.Contains("vector(256)", ex.Message);
    }
}

[Collection(InfraCollection.Name)]
public sealed class RedisAndMongoTests(InfraFixture infra) : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync() => _host = await infra.StartInfraHostAsync();

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        await ((IAsyncDisposable)_host).DisposeAsync();
    }

    [Fact]
    public async Task EmbeddingCache_SecondCallIsServedFromRedis()
    {
        var emb = _host.Services.GetRequiredService<IEmbeddingService>();
        var stats = _host.Services.GetRequiredService<CacheStats>();
        var texts = new[] { $"önbellek testi {Guid.NewGuid()}", $"ikinci metin {Guid.NewGuid()}" };

        var first = await emb.EmbedAsync(texts, EmbeddingPurpose.Document);
        var hitsBefore = (long)stats.Snapshot().GetType().GetProperty("embeddingHits")!.GetValue(stats.Snapshot())!;
        var second = await emb.EmbedAsync(texts, EmbeddingPurpose.Document);
        var hitsAfter = (long)stats.Snapshot().GetType().GetProperty("embeddingHits")!.GetValue(stats.Snapshot())!;

        Assert.Equal(2, hitsAfter - hitsBefore);
        Assert.Equal(first[0], second[0]);
        Assert.Equal(first[1], second[1]);
    }

    [Fact]
    public async Task AnswerCache_RoundTrips()
    {
        var cache = _host.Services.GetRequiredService<IAnswerCache>();
        var key = $"answer:test:{Guid.NewGuid():N}";
        Assert.Null(await cache.GetAsync(key, default));
        await cache.SetAsync(key, new CachedAnswer("yanıt [1]", new Verdict(VerdictLabel.Grounded, 1, [1], [], "lexical")), default);
        var got = await cache.GetAsync(key, default);
        Assert.Equal("yanıt [1]", got!.Answer);
        Assert.Equal(VerdictLabel.Grounded, got.Verdict.Label);
    }

    [Fact]
    public async Task ChatHistory_IsOrderedAndIsolatedPerUser()
    {
        var store = _host.Services.GetRequiredService<IChatHistoryStore>();
        var session = Guid.NewGuid().ToString("N");
        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
            await store.AppendAsync("alice", session, new ChatTurn($"s{i}", $"y{i}", t0.AddSeconds(i)), default);

        var recent = await store.GetRecentAsync("alice", session, 3, default);
        Assert.Equal(["s2", "s3", "s4"], recent.Select(t => t.Question));
        Assert.Empty(await store.GetRecentAsync("mallory", session, 10, default));
    }

    [Fact]
    public async Task AuditLog_PersistsEntries()
    {
        var audit = _host.Services.GetRequiredService<MongoAuditLog>();
        var q = $"audit {Guid.NewGuid()}";
        await audit.WriteAsync(new AuditEntry("alice", "s", q, q, [Guid.NewGuid()], VerdictLabel.Partial, 0.5,
            new ChatMetrics(1, 2, 3, 100, 4096, 1, 0, 0, 0, 0, false, q), "m", DateTimeOffset.UtcNow), default);
        var recent = await audit.RecentAsync(20, default);
        Assert.Contains(recent, a => a.Question == q && a.Verdict == "Partial");
    }
}
