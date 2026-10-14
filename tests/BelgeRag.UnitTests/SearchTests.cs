using BelgeRag.Core.Embeddings;
using BelgeRag.Core.Search;

namespace BelgeRag.UnitTests;

public class ReciprocalRankFusionTests
{
    private static SearchHit Hit(string id) => new(Guid.Parse(id.PadLeft(32, '0')), Guid.Empty, "t", 0, id, 0);

    [Fact]
    public void DocumentInBothLists_OutranksDocumentsInOne()
    {
        var a = Hit("a"); var b = Hit("b"); var c = Hit("c");
        var fused = ReciprocalRankFusion.Fuse([a, b], [c, b]);

        Assert.Equal(b.ChunkId, fused[0].ChunkId);
        Assert.Equal(2, fused[0].VectorRank);
        Assert.Equal(2, fused[0].TextRank);
        Assert.Equal(1.0 / 62 + 1.0 / 62, fused[0].Score, 10);
    }

    [Fact]
    public void Ties_AreBrokenByBestSingleRank()
    {
        var a = Hit("a"); var c = Hit("c");
        var fused = ReciprocalRankFusion.Fuse([a], [c]);
        Assert.Equal(2, fused.Count);
        Assert.Equal(fused[0].Score, fused[1].Score);
        Assert.Equal(1, fused[0].VectorRank ?? fused[0].TextRank);
    }

    [Fact]
    public void EmptyInputs_GiveEmptyResult() => Assert.Empty(ReciprocalRankFusion.Fuse([], []));
}

public class LexicalRerankerTests
{
    [Fact]
    public async Task CandidateCoveringAllQueryTerms_MovesToTop()
    {
        SearchHit H(string text) => new(Guid.NewGuid(), Guid.NewGuid(), "d", 0, text, 0);
        var candidates = new[]
        {
            H("Docker imajları küçük tutulur ve önbellek katmanları kullanılır."),
            H("Kuyruk mesajları tekrar denenir."),
            H("RabbitMQ dead-letter kuyruğu, başarısız mesajları tekrar deneme sonrası toplar."),
        };
        var ranked = await new LexicalReranker().RerankAsync("RabbitMQ dead-letter kuyruğu ne işe yarar", candidates, default);
        Assert.Equal(candidates[2].ChunkId, ranked[0].ChunkId);
    }

    [Fact]
    public async Task QueryWithoutContentTerms_KeepsOrder()
    {
        SearchHit H(string text) => new(Guid.NewGuid(), Guid.NewGuid(), "d", 0, text, 0);
        var candidates = new[] { H("bir"), H("iki") };
        var ranked = await new LexicalReranker().RerankAsync("ve ile", candidates, default);
        Assert.Same(candidates, ranked);
    }
}

public class HybridSearchServiceTests
{
    private static async Task<(HybridSearchService, InMemorySearchIndex)> Build()
    {
        var emb = new HashingEmbeddingService(256);
        var index = new InMemorySearchIndex(emb);
        await index.AddAsync("blade-fem", "Türbin kanadı termal gerilme analizi sonlu elemanlarla yapılır.");
        await index.AddAsync("polip", "Kolonoskopi görüntülerinde polip segmentasyonu U-Net ile yapılır.");
        await index.AddAsync("sonar", "Pasif sonar kayıtlarından gemi sınıflandırması yapılır.");
        return (new HybridSearchService(index, emb, new LexicalReranker(), new HybridSearchOptions()), index);
    }

    [Theory]
    [InlineData(SearchMode.Hybrid, 1, 1)]
    [InlineData(SearchMode.Vector, 1, 0)]
    [InlineData(SearchMode.FullText, 0, 1)]
    public async Task Mode_SelectsRetrievers(SearchMode mode, int vectorCalls, int textCalls)
    {
        var (svc, index) = await Build();
        await svc.SearchAsync(new SearchRequest("polip segmentasyonu", 2, mode));
        Assert.Equal(vectorCalls, index.VectorCalls);
        Assert.Equal(textCalls, index.TextCalls);
    }

    [Fact]
    public async Task Hybrid_FindsRelevantDocument_AndRespectsTopK()
    {
        var (svc, _) = await Build();
        var r = await svc.SearchAsync(new SearchRequest("kanat gerilmesi nasıl hesaplanır", 2));
        Assert.Equal("blade-fem", r.Hits[0].DocumentTitle);
        Assert.True(r.Hits.Count <= 2);
        Assert.True(r.Timings.TotalMs >= 0);
    }
}
