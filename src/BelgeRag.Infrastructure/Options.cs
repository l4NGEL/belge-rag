namespace BelgeRag.Infrastructure;

public sealed record PostgresOptions
{
    public const string Section = "Postgres";
    public string ConnectionString { get; init; } = "Host=localhost;Port=5432;Database=belgerag;Username=belgerag;Password=belgerag";
}

public sealed record RedisOptions
{
    public const string Section = "Redis";
    public string Configuration { get; init; } = "localhost:6379";
    public TimeSpan EmbeddingTtl { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan AnswerTtl { get; init; } = TimeSpan.FromHours(6);
}

public sealed record MongoOptions
{
    public const string Section = "Mongo";
    public string ConnectionString { get; init; } = "mongodb://localhost:27017";
    public string Database { get; init; } = "belgerag";
}

public sealed record RabbitMqOptions
{
    public const string Section = "RabbitMq";
    public string Uri { get; init; } = "amqp://guest:guest@localhost:5672/";
    public string Exchange { get; init; } = "belge.ingest";
    public string Queue { get; init; } = "belge.ingest.q";
    public string RoutingKey { get; init; } = "document.uploaded";
    public string DeadLetterExchange { get; init; } = "belge.ingest.dlx";
    public string DeadLetterQueue { get; init; } = "belge.ingest.dlq";
    public ushort Prefetch { get; init; } = 8;
    public ushort ConsumerConcurrency { get; init; } = 2;
    public int MaxRetries { get; init; } = 3;
}

public enum LlmProvider
{
    /// <summary>Model gerektirmeyen çıkarımsal temel çizgi (kredisiz/çevrimdışı, testler).</summary>
    Extractive,

    /// <summary>Ollama'nın OpenAI-uyumlu /v1 uç noktası.</summary>
    Ollama,

    /// <summary>OpenAI veya OpenAI-uyumlu herhangi bir API.</summary>
    OpenAI,
}

public enum EmbeddingProvider
{
    Hashing,
    Ollama,
    OpenAI,
}

public sealed record LlmOptions
{
    public const string Section = "Llm";
    public LlmProvider Provider { get; init; } = LlmProvider.Extractive;
    public string ChatModel { get; init; } = "gemma3:4b";
    public string Endpoint { get; init; } = "http://localhost:11434/v1";
    public string? ApiKey { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    public bool CondenseWithLlm { get; init; } = true;
    public bool VerifyWithLlm { get; init; }
}

public sealed record EmbeddingOptions
{
    public const string Section = "Embeddings";
    public EmbeddingProvider Provider { get; init; } = EmbeddingProvider.Hashing;
    public string Model { get; init; } = "nomic-embed-text";
    public string Endpoint { get; init; } = "http://localhost:11434/v1";
    public string? ApiKey { get; init; }
    public int Dimensions { get; init; } = 384;

    /// <summary>Sorguya eklenecek görev öneki (nomic-embed-text için "search_query: ").</summary>
    public string QueryPrefix { get; init; } = "";

    /// <summary>Belge parçasına eklenecek görev öneki (nomic-embed-text için "search_document: ").</summary>
    public string DocumentPrefix { get; init; } = "";
}
