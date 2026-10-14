using BelgeRag.Core.Embeddings;
using BelgeRag.Core.Text;

namespace BelgeRag.UnitTests;

public class TextFoldingTests
{
    [Theory]
    [InlineData("İSTANBUL", "istanbul")]
    [InlineData("ışık ŞEKER ğüöç", "isik seker guoc")]
    [InlineData("ID Card", "id card")]
    public void Fold_TurkishAndAsciiLettersCollapseToSameForm(string input, string expected) =>
        Assert.Equal(expected, TextFolding.Fold(input));

    [Fact]
    public void ContentTerms_DropsStopWordsAndShortTokens_AndTruncatesToStem()
    {
        var terms = TextFolding.ContentTerms("Bu model ve modelleri için doğrulama yapılır").ToList();
        Assert.Equal(["model", "modell", "dogrul"], terms);
    }
}

public class TextChunkerTests
{
    private readonly TiktokenTokenCounter _tokens = new();

    private static string Paragraphs(int n) => string.Join("\n\n", Enumerable.Range(1, n).Select(i =>
        $"Paragraf {i} birinci cümle burada yer alıyor. Paragraf {i} ikinci cümle biraz daha uzun ve ek bilgi içeriyor. " +
        $"Paragraf {i} üçüncü cümle de sonucu özetliyor."));

    [Fact]
    public void Chunks_StayWithinTokenLimit_AndAreOrdered()
    {
        var chunker = new TextChunker(_tokens);
        var chunks = chunker.Chunk(Paragraphs(40), new ChunkingOptions { MaxTokens = 120, OverlapTokens = 20 });

        Assert.True(chunks.Count > 5);
        Assert.All(chunks, c => Assert.InRange(c.TokenCount, 1, 120));
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));
    }

    [Fact]
    public void ConsecutiveChunks_Overlap()
    {
        var chunker = new TextChunker(_tokens);
        var chunks = chunker.Chunk(Paragraphs(20), new ChunkingOptions { MaxTokens = 100, OverlapTokens = 30 });

        for (var i = 1; i < chunks.Count; i++)
        {
            var lastSentenceOfPrev = chunks[i - 1].Text.Split(". ").Last().Trim();
            Assert.Contains(lastSentenceOfPrev.TrimEnd('.'), chunks[i].Text);
        }
    }

    [Fact]
    public void Headings_ArePrefixed_AndOverlapDoesNotCrossSections()
    {
        var text = "# Kurulum\nDocker kurulur. Compose çalıştırılır.\n\n# Testler\nxUnit testleri koşulur.";
        var chunks = new TextChunker(_tokens).Chunk(text, new ChunkingOptions { MaxTokens = 64, OverlapTokens = 16 });

        Assert.Equal(2, chunks.Count);
        Assert.StartsWith("Kurulum\n", chunks[0].Text);
        Assert.StartsWith("Testler\n", chunks[1].Text);
        Assert.DoesNotContain("Docker", chunks[1].Text);
    }

    [Fact]
    public void OverlongSentence_IsSplitIntoWordWindows()
    {
        var sentence = string.Join(" ", Enumerable.Range(0, 400).Select(i => $"kelime{i}"));
        var chunks = new TextChunker(_tokens).Chunk(sentence, new ChunkingOptions { MaxTokens = 64, OverlapTokens = 8 });

        Assert.True(chunks.Count > 4);
        Assert.All(chunks, c => Assert.True(c.TokenCount <= 64));
        Assert.Contains("kelime399", chunks[^1].Text);
    }

    [Fact]
    public void InvalidOptions_Throw()
    {
        var chunker = new TextChunker(_tokens);
        Assert.Throws<ArgumentException>(() => chunker.Chunk("x", new ChunkingOptions { MaxTokens = 100, OverlapTokens = 60 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => chunker.Chunk("x", new ChunkingOptions { MaxTokens = 4 }));
    }

    [Fact]
    public void Truncate_RespectsTokenBudget()
    {
        var text = string.Concat(Enumerable.Repeat("token sayımı testi ", 100));
        var cut = _tokens.Truncate(text, 25);
        Assert.True(_tokens.Count(cut) <= 25);
        Assert.StartsWith(cut, text);
    }
}

public class HashingEmbeddingTests
{
    private static float Cos(float[] a, float[] b) => a.Zip(b, (x, y) => x * y).Sum();

    [Fact]
    public async Task IsDeterministicAcrossInstances_AndUnitLength()
    {
        var a = await new HashingEmbeddingService(384).EmbedQueryAsync("pgvector ile hibrit arama");
        var b = await new HashingEmbeddingService(384).EmbedQueryAsync("pgvector ile hibrit arama");
        Assert.Equal(a, b);
        Assert.Equal(1.0, Math.Sqrt(a.Sum(x => x * x)), 4);
    }

    [Fact]
    public async Task InflectedForms_AreCloserThanUnrelatedText()
    {
        var svc = new HashingEmbeddingService(384);
        var q = await svc.EmbedQueryAsync("türbin kanadı gerilme analizi");
        var related = await svc.EmbedQueryAsync("Türbin kanatlarında termal gerilmenin sonlu eleman analizleri");
        var unrelated = await svc.EmbedQueryAsync("kolonoskopi görüntülerinde polip segmentasyonu");
        Assert.True(Cos(q, related) > Cos(q, unrelated) + 0.1);
    }

    [Fact]
    public async Task EmptyText_GivesZeroVector()
    {
        var v = await new HashingEmbeddingService(64).EmbedQueryAsync("ve ile bu");
        Assert.All(v, x => Assert.Equal(0f, x));
    }
}
