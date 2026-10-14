using System.Text.Json;
using BelgeRag.Api.Auth;
using BelgeRag.Api.Sse;
using BelgeRag.Core.Chat;
using BelgeRag.Core.Embeddings;
using BelgeRag.Core.Ingestion;
using BelgeRag.Core.Search;
using BelgeRag.Core.Text;
using BelgeRag.Infrastructure;
using BelgeRag.Infrastructure.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.SemanticKernel;
using Npgsql;

namespace BelgeRag.UnitTests;

public class SseWriterTests
{
    [Fact]
    public void Format_ProducesNamedEventWithSingleLineJson()
    {
        var frame = SseWriter.Format(new TokenEvent("merhaba\ndünya"));
        Assert.StartsWith("event: token\ndata: {", frame);
        Assert.EndsWith("}\n\n", frame);
        Assert.Equal(3, frame.Split('\n').Length - 1); // event, data, boş satır

        var json = frame.Split('\n')[1]["data: ".Length..];
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("token", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("merhaba\ndünya", doc.RootElement.GetProperty("text").GetString());
        Assert.False(doc.RootElement.TryGetProperty("eventName", out _)); // olay adı SSE başlığında, gövdede değil
    }

    [Fact]
    public void Format_SerializesEnumsAsStrings()
    {
        var frame = SseWriter.Format(new VerdictEvent(new Verdict(VerdictLabel.Partial, 0.5, [1], [], "lexical")));
        Assert.Contains("\"label\":\"Partial\"", frame);
    }
}

public class JwtTokenServiceTests
{
    private static readonly JwtOptions Options = new()
    {
        SigningKey = "unit-test-signing-key-0123456789-abcdef",
        Users = [new DemoUser { Username = "admin", Password = "pw", Role = Roles.Admin }],
    };

    [Fact]
    public async Task Login_IssuesTokenThatValidates_WithRoleClaim()
    {
        var svc = new JwtTokenService(Options, TimeProvider.System);
        var issued = svc.Login("admin", "pw");
        Assert.NotNull(issued);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(issued.AccessToken, svc.ValidationParameters());
        Assert.True(result.IsValid);
        Assert.True(result.ClaimsIdentity.HasClaim(System.Security.Claims.ClaimTypes.Role, Roles.Admin));
    }

    [Theory]
    [InlineData("admin", "wrong")]
    [InlineData("ghost", "pw")]
    public void Login_RejectsBadCredentials(string user, string pw) =>
        Assert.Null(new JwtTokenService(Options, TimeProvider.System).Login(user, pw));

    [Fact]
    public async Task TokenSignedWithOtherKey_IsRejected()
    {
        var other = new JwtTokenService(Options with { SigningKey = "another-signing-key-0123456789-abcdef" }, TimeProvider.System);
        var token = other.Issue("admin", Roles.Admin).AccessToken;
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token,
            new JwtTokenService(Options, TimeProvider.System).ValidationParameters());
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ShortSigningKey_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            new JwtTokenService(Options with { SigningKey = "short" }, TimeProvider.System).Issue("a", "b"));
}

public class CompositionRootTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?>? extra = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(extra ?? []).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBelgeRag(config, "test");
        // Bağlantılar tembel; ValidateOnBuild çözülebilirliği ve kapsam ihlallerini broker/DB olmadan doğrular.
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void Container_BuildsWithScopeValidation()
    {
        using var sp = BuildProvider();
        using var scope = sp.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RagChatOrchestrator>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<DocumentIngestionService>());
    }

    [Fact]
    public void Lifetimes_AreAsDesigned()
    {
        using var sp = BuildProvider();
        using var s1 = sp.CreateScope();
        using var s2 = sp.CreateScope();

        // Singleton: havuzlar ve durumsuz algoritmalar süreç boyunca tek.
        Assert.Same(s1.ServiceProvider.GetRequiredService<NpgsqlDataSource>(), s2.ServiceProvider.GetRequiredService<NpgsqlDataSource>());
        Assert.Same(s1.ServiceProvider.GetRequiredService<ITokenCounter>(), s2.ServiceProvider.GetRequiredService<ITokenCounter>());
        Assert.Same(s1.ServiceProvider.GetRequiredService<IEmbeddingService>(), s2.ServiceProvider.GetRequiredService<IEmbeddingService>());

        // Scoped: kapsam içinde aynı, kapsamlar arası farklı.
        var a = s1.ServiceProvider.GetRequiredService<HybridSearchService>();
        Assert.Same(a, s1.ServiceProvider.GetRequiredService<HybridSearchService>());
        Assert.NotSame(a, s2.ServiceProvider.GetRequiredService<HybridSearchService>());

        // Transient: SK Kernel her çözümlemede yeni (eklentiler sızmasın).
        Assert.NotSame(s1.ServiceProvider.GetRequiredService<Kernel>(), s1.ServiceProvider.GetRequiredService<Kernel>());
    }

    [Fact]
    public void Embeddings_AreWrappedByRedisCache_ForAnyProvider()
    {
        using var sp = BuildProvider(new() { ["Embeddings:Dimensions"] = "128" });
        var emb = sp.GetRequiredService<IEmbeddingService>();
        Assert.IsType<CachedEmbeddingService>(emb);
        Assert.Equal("hashing-ngram-128", emb.ModelId);
    }

    [Fact]
    public void OllamaProvider_RegistersSemanticKernelChatService()
    {
        using var sp = BuildProvider(new() { ["Llm:Provider"] = "Ollama", ["Llm:ChatModel"] = "gemma3:4b" });
        var info = sp.GetRequiredService<ChatModelInfo>();
        Assert.Equal("gemma3:4b", info.ChatModel);
        var chat = sp.GetRequiredService<Kernel>().GetRequiredService<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>();
        Assert.Equal("OpenAIChatCompletionService", chat.GetType().Name);
    }
}

public class SemanticKernelEmbeddingServiceTests
{
    /// <summary>Girdiyi kaydeden ve sabit vektör dönen sahte üretici.</summary>
    private sealed class RecordingGenerator(int dims) : Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>
    {
        public List<string> Seen { get; } = [];

        public Task<Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, Microsoft.Extensions.AI.EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = values.ToList();
            Seen.AddRange(list);
            return Task.FromResult(new Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>(
                list.Select(_ => new Microsoft.Extensions.AI.Embedding<float>(Enumerable.Repeat(2f, dims).ToArray()))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task AppliesTaskPrefixPerPurpose_NormalizesAndTagsModelId()
    {
        var gen = new RecordingGenerator(4);
        var svc = new BelgeRag.Infrastructure.Llm.SemanticKernelEmbeddingService(gen, "nomic", 4, "search_query: ", "search_document: ");

        var q = await svc.EmbedQueryAsync("soru");
        await svc.EmbedAsync(["parça"], EmbeddingPurpose.Document);

        Assert.Equal(["search_query: soru", "search_document: parça"], gen.Seen);
        Assert.Equal(1.0, Math.Sqrt(q.Sum(x => x * x)), 5);
        Assert.Equal("nomic+prefix", svc.ModelId);
    }

    [Fact]
    public async Task WrongDimension_Throws()
    {
        var svc = new BelgeRag.Infrastructure.Llm.SemanticKernelEmbeddingService(new RecordingGenerator(3), "m", 4);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.EmbedQueryAsync("x"));
    }
}

public class DocumentIngestionServiceTests
{
    [Fact]
    public async Task Ingest_ChunksEmbedsAndWrites_Idempotently()
    {
        var repo = new InMemoryDocuments();
        var text = string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"Bölüm {i} hakkında birkaç cümlelik bir açıklama. Ek ayrıntı {i}."));
        var id = await repo.CreateAsync("kılavuz", text, "admin", default);
        var svc = new DocumentIngestionService(repo, new TextChunker(new TiktokenTokenCounter()), new HashingEmbeddingService(64),
            new ChunkingOptions { MaxTokens = 80, OverlapTokens = 16 }, NullLogger<DocumentIngestionService>.Instance);

        var first = await svc.IngestAsync(id, default);
        var second = await svc.IngestAsync(id, default);

        Assert.NotNull(first);
        Assert.Equal(first.Chunks, second!.Chunks);
        Assert.Equal(DocumentStatus.Indexed, repo.Docs[id].Status);
        Assert.All(repo.Chunks[id], c => Assert.Equal(64, c.Embedding.Length));
        Assert.Equal("hashing-ngram-64", repo.Docs[id].EmbeddingModel);
    }

    [Fact]
    public async Task MissingDocument_IsSkipped()
    {
        var svc = new DocumentIngestionService(new InMemoryDocuments(), new TextChunker(new TiktokenTokenCounter()),
            new HashingEmbeddingService(64), new ChunkingOptions(), NullLogger<DocumentIngestionService>.Instance);
        Assert.Null(await svc.IngestAsync(Guid.NewGuid(), default));
    }
}
