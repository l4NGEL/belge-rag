using BelgeRag.Infrastructure;
using BelgeRag.Worker;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.MongoDb;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace BelgeRag.IntegrationTests;

/// <summary>
/// Dört bağımlılığı gerçek container'larda paralel başlatır; koleksiyondaki tüm testler paylaşır.
/// İmajlar docker-compose.yml ile aynıdır.
/// </summary>
public sealed class InfraFixture : IAsyncLifetime
{
    public const int Dimensions = 256;

    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg16").WithDatabase("belgerag").WithUsername("belgerag").WithPassword("belgerag").Build();

    public RedisContainer Redis { get; } = new RedisBuilder().WithImage("redis:8-alpine").Build();

    public MongoDbContainer Mongo { get; } = new MongoDbBuilder().WithImage("mongo:7.0").Build();

    public RabbitMqContainer Rabbit { get; } = new RabbitMqBuilder().WithImage("rabbitmq:4.1-management-alpine").Build();

    public Dictionary<string, string?> Settings => new()
    {
        ["Postgres:ConnectionString"] = Postgres.GetConnectionString(),
        ["Redis:Configuration"] = Redis.GetConnectionString(),
        ["Mongo:ConnectionString"] = Mongo.GetConnectionString(),
        ["Mongo:Database"] = "belgerag_test",
        ["RabbitMq:Uri"] = Rabbit.GetConnectionString(),
        ["RabbitMq:MaxRetries"] = "1",
        ["Embeddings:Provider"] = "Hashing",
        ["Embeddings:Dimensions"] = Dimensions.ToString(),
        ["Llm:Provider"] = "Extractive",
        ["Chunking:MaxTokens"] = "120",
        ["Chunking:OverlapTokens"] = "24",
        ["Auth:SigningKey"] = "integration-test-signing-key-0123456789",
        ["Auth:Users:0:Username"] = "admin",
        ["Auth:Users:0:Password"] = "admin-pw",
        ["Auth:Users:0:Role"] = "admin",
        ["Auth:Users:1:Username"] = "reader",
        ["Auth:Users:1:Password"] = "reader-pw",
        ["Auth:Users:1:Role"] = "user",
        ["RateLimit:ChatPerMinute"] = "1000",
    };

    public Task InitializeAsync() =>
        Task.WhenAll(Postgres.StartAsync(), Redis.StartAsync(), Mongo.StartAsync(), Rabbit.StartAsync());

    public async Task DisposeAsync()
    {
        await Task.WhenAll(
            Postgres.DisposeAsync().AsTask(), Redis.DisposeAsync().AsTask(),
            Mongo.DisposeAsync().AsTask(), Rabbit.DisposeAsync().AsTask());
    }

    /// <summary>Altyapıya bağlı (initializer çalışmış) bir DI kapsayıcısı; worker/API olmadan bileşen testi için.</summary>
    public async Task<IHost> StartInfraHostAsync()
    {
        var b = Host.CreateApplicationBuilder();
        b.Configuration.AddInMemoryCollection(Settings);
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        b.Services.AddBelgeRag(b.Configuration, "it-infra");
        var host = b.Build();
        await host.StartAsync();
        return host;
    }

    public async Task<IHost> StartWorkerAsync()
    {
        var b = Host.CreateApplicationBuilder();
        b.Configuration.AddInMemoryCollection(Settings);
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        b.Services.AddBelgeRag(b.Configuration, "it-worker");
        b.Services.AddHostedService<IngestConsumer>();
        var host = b.Build();
        await host.StartAsync();
        return host;
    }
}

[CollectionDefinition(Name)]
public sealed class InfraCollection : ICollectionFixture<InfraFixture>
{
    public const string Name = "infra";
}

public sealed class ApiFactory(InfraFixture infra) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (k, v) in infra.Settings) builder.UseSetting(k, v);
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
    }
}
