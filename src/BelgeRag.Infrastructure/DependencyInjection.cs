using System.ClientModel;
using BelgeRag.Core.Agents;
using BelgeRag.Core.Chat;
using BelgeRag.Core.Embeddings;
using BelgeRag.Core.Ingestion;
using BelgeRag.Core.Llm;
using BelgeRag.Core.Prompting;
using BelgeRag.Core.Search;
using BelgeRag.Core.Text;
using BelgeRag.Infrastructure.Llm;
using BelgeRag.Infrastructure.Messaging;
using BelgeRag.Infrastructure.Mongo;
using BelgeRag.Infrastructure.Postgres;
using BelgeRag.Infrastructure.Redis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using MongoDB.Driver;
using Npgsql;
using OpenAI;
using Pgvector.Npgsql;
using StackExchange.Redis;

namespace BelgeRag.Infrastructure;

/// <summary>
/// Yaşam döngüsü kuralları:
/// <list type="bullet">
/// <item><b>Singleton</b> — iş parçacığı güvenli, kurulumu pahalı, istekler arası paylaşılması gereken nesneler:
/// bağlantı havuzları (NpgsqlDataSource, IConnectionMultiplexer, IMongoClient, AMQP bağlantısı), tokenizer,
/// embedding istemcisi, durumsuz algoritmalar (chunker, bütçeleyici, skorlayıcı), sayaçlar.</item>
/// <item><b>Scoped</b> — bir HTTP isteği ya da bir kuyruk mesajı ölçeğindeki iş birimi: depolar, arama,
/// ajanlar, orkestratör, ingestion servisi. Worker her mesaj için yeni kapsam açar.</item>
/// <item><b>Transient</b> — Semantic Kernel <see cref="Kernel"/>: ajanlar ona eklenti yükler, paylaşılırsa
/// eklentiler istekler arasında sızar; SK de bu yüzden Kernel'i transient önerir.</item>
/// </list>
/// Singleton'lar Scoped bağımlılık almaz (captive dependency); ValidateScopes/ValidateOnBuild ile ve
/// birim testiyle doğrulanır.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddBelgeRag(this IServiceCollection services, IConfiguration config, string clientName)
    {
        var pg = config.GetSection(PostgresOptions.Section).Get<PostgresOptions>() ?? new();
        var redis = config.GetSection(RedisOptions.Section).Get<RedisOptions>() ?? new();
        var mongo = config.GetSection(MongoOptions.Section).Get<MongoOptions>() ?? new();
        var rabbit = config.GetSection(RabbitMqOptions.Section).Get<RabbitMqOptions>() ?? new();
        var llm = config.GetSection(LlmOptions.Section).Get<LlmOptions>() ?? new();
        var emb = config.GetSection(EmbeddingOptions.Section).Get<EmbeddingOptions>() ?? new();
        var chunking = config.GetSection("Chunking").Get<ChunkingOptions>() ?? new();
        var budget = config.GetSection("TokenBudget").Get<TokenBudgetOptions>() ?? new();
        var hybrid = config.GetSection("Search").Get<HybridSearchOptions>() ?? new();

        services.AddSingleton(pg).AddSingleton(redis).AddSingleton(mongo).AddSingleton(rabbit)
            .AddSingleton(llm).AddSingleton(emb).AddSingleton(chunking).AddSingleton(budget).AddSingleton(hybrid);
        var generative = llm.Provider != LlmProvider.Extractive;
        services.AddSingleton(new AgentOptions
        {
            CondenseWithLlm = generative && llm.CondenseWithLlm,
            VerifyWithLlm = generative && llm.VerifyWithLlm,
        });

        // ---------- Singleton ----------
        services.AddSingleton<ITokenCounter, TiktokenTokenCounter>();
        services.AddSingleton<TextChunker>();
        services.AddSingleton<ContextBudgeter>();
        services.AddSingleton<GroundingScorer>();
        services.AddSingleton<CacheStats>();
        services.AddSingleton<IReranker, LexicalReranker>();

        services.AddSingleton(_ =>
        {
            var b = new NpgsqlDataSourceBuilder(pg.ConnectionString);
            b.UseVector();
            return b.Build();
        });
        services.AddSingleton(sp => new PgSchema(sp.GetRequiredService<NpgsqlDataSource>(), emb.Dimensions));

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var o = ConfigurationOptions.Parse(redis.Configuration);
            o.AbortOnConnectFail = false; // Redis geç kalkarsa arka planda yeniden bağlan
            return ConnectionMultiplexer.Connect(o);
        });
        services.AddSingleton<IAnswerCache, RedisAnswerCache>();

        services.AddSingleton<IMongoClient>(_ => new MongoClient(mongo.ConnectionString));
        services.AddSingleton<MongoContext>();
        services.AddSingleton<IChatHistoryStore, MongoChatHistoryStore>();
        services.AddSingleton<MongoAuditLog>();
        services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<MongoAuditLog>());

        services.AddSingleton(_ => new RabbitMqConnection(rabbit, clientName));
        services.AddSingleton<RabbitMqIngestQueue>();
        services.AddSingleton<IIngestQueue>(sp => sp.GetRequiredService<RabbitMqIngestQueue>());

        // ---------- Semantic Kernel: sağlayıcı seçimi ----------
        var kernel = services.AddKernel(); // Kernel: Transient
        string chatModel;
        if (llm.Provider == LlmProvider.Extractive)
        {
            services.AddSingleton<IChatCompletionService, ExtractiveChatCompletionService>();
            chatModel = ExtractiveChatCompletionService.ModelId;
        }
        else
        {
            kernel.AddOpenAIChatCompletion(llm.ChatModel, CreateClient(llm.Endpoint, llm.ApiKey, llm.Timeout));
            chatModel = llm.ChatModel;
        }
        services.AddSingleton(new ChatModelInfo(chatModel, llm.Provider.ToString()));

        if (emb.Provider == EmbeddingProvider.Hashing)
        {
            services.AddKeyedSingleton<IEmbeddingService>("inner", (_, _) => new HashingEmbeddingService(emb.Dimensions));
        }
        else
        {
            // Ollama 'dimensions' parametresini desteklemez; OpenAI text-embedding-3-* destekler.
            int? dims = emb.Provider == EmbeddingProvider.OpenAI ? emb.Dimensions : null;
            kernel.AddOpenAIEmbeddingGenerator(emb.Model, CreateClient(emb.Endpoint, emb.ApiKey, llm.Timeout), dims);
            services.AddKeyedSingleton<IEmbeddingService>("inner", (sp, _) => new SemanticKernelEmbeddingService(
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(), emb.Model, emb.Dimensions,
                emb.QueryPrefix, emb.DocumentPrefix));
        }
        // Dekoratör: Redis önbelleği her sağlayıcının önüne konur.
        services.AddSingleton<IEmbeddingService>(sp => new CachedEmbeddingService(
            sp.GetRequiredKeyedService<IEmbeddingService>("inner"), sp.GetRequiredService<IConnectionMultiplexer>(),
            redis, sp.GetRequiredService<CacheStats>(), sp.GetRequiredService<ILogger<CachedEmbeddingService>>()));

        // ---------- Scoped ----------
        services.AddScoped<IDocumentRepository, PgDocumentRepository>();
        services.AddScoped<ISearchIndex, PgSearchIndex>();
        services.AddScoped<HybridSearchService>();
        services.AddScoped<KnowledgeBasePlugin>();
        services.AddScoped<RetrieverAgent>();
        services.AddScoped<AnswerAgent>();
        services.AddScoped<VerifierAgent>();
        services.AddScoped<RagChatOrchestrator>();
        services.AddScoped<DocumentIngestionService>();

        services.AddHostedService<InfrastructureInitializer>();
        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres")
            .AddCheck<RedisHealthCheck>("redis")
            .AddCheck<MongoHealthCheck>("mongo")
            .AddCheck<RabbitMqHealthCheck>("rabbitmq");
        return services;
    }

    private static OpenAIClient CreateClient(string endpoint, string? apiKey, TimeSpan timeout) =>
        new(new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? "ollama" : apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint), NetworkTimeout = timeout });
}

/// <summary>Başlangıçta şema/indeks/kuyruk topolojisini kurar; bağımlılıklar geç kalkarsa üstel bekleme ile yeniden dener.</summary>
public sealed class InfrastructureInitializer(
    PgSchema schema,
    MongoContext mongo,
    RabbitMqConnection rabbit,
    ILogger<InfrastructureInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await schema.EnsureAsync(ct);
                await mongo.EnsureIndexesAsync(ct);
                await rabbit.DeclareTopologyAsync(ct);
                logger.LogInformation("Altyapı hazır (deneme {Attempt})", attempt);
                return;
            }
            catch (Exception e) when (attempt < 8 && e is not InvalidOperationException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Altyapı henüz hazır değil ({Error}); {Delay} sonra tekrar", e.Message, delay);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class PostgresHealthCheck(NpgsqlDataSource ds) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = ds.CreateCommand("SELECT count(*) FROM chunks");
            var chunks = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            return HealthCheckResult.Healthy($"{chunks} chunk");
        }
        catch (Exception e) { return HealthCheckResult.Unhealthy(e.Message); }
    }
}

public sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try { return HealthCheckResult.Healthy($"ping {(await redis.GetDatabase().PingAsync()).TotalMilliseconds:F1} ms"); }
        catch (Exception e) { return HealthCheckResult.Unhealthy(e.Message); }
    }
}

public sealed class MongoHealthCheck(MongoContext mongo) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try { await mongo.PingAsync(ct); return HealthCheckResult.Healthy(); }
        catch (Exception e) { return HealthCheckResult.Unhealthy(e.Message); }
    }
}

public sealed class RabbitMqHealthCheck(RabbitMqConnection rabbit) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var conn = await rabbit.GetAsync(ct);
            await using var ch = await conn.CreateChannelAsync(cancellationToken: ct);
            var q = await ch.QueueDeclarePassiveAsync(rabbit.Options.Queue, ct);
            return HealthCheckResult.Healthy($"kuyrukta {q.MessageCount} mesaj, {q.ConsumerCount} tüketici");
        }
        catch (Exception e) { return HealthCheckResult.Unhealthy(e.Message); }
    }
}
