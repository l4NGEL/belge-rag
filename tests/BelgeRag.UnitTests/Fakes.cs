using System.Collections.Concurrent;
using BelgeRag.Core.Agents;
using BelgeRag.Core.Chat;
using BelgeRag.Core.Embeddings;
using BelgeRag.Core.Ingestion;
using BelgeRag.Core.Llm;
using BelgeRag.Core.Prompting;
using BelgeRag.Core.Search;
using BelgeRag.Core.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace BelgeRag.UnitTests;

/// <summary>pgvector + tsvector'ün bellek içi karşılığı: kosinüs ve terim örtüşmesi.</summary>
internal sealed class InMemorySearchIndex(IEmbeddingService embeddings) : ISearchIndex
{
    private readonly List<(SearchHit Hit, float[] Vector)> _rows = [];
    public int VectorCalls, TextCalls;

    public async Task AddAsync(string title, params string[] chunks)
    {
        var docId = Guid.NewGuid();
        var vectors = await embeddings.EmbedAsync(chunks, EmbeddingPurpose.Document);
        for (var i = 0; i < chunks.Length; i++)
            _rows.Add((new SearchHit(Guid.NewGuid(), docId, title, i, chunks[i], 0), vectors[i]));
    }

    public Task<IReadOnlyList<SearchHit>> VectorSearchAsync(float[] q, string model, int limit, CancellationToken ct)
    {
        Interlocked.Increment(ref VectorCalls);
        IReadOnlyList<SearchHit> r = _rows
            .Select(r => r.Hit with { Score = r.Vector.Zip(q, (a, b) => a * b).Sum() })
            .OrderByDescending(h => h.Score).Take(limit).ToList();
        return Task.FromResult(r);
    }

    public Task<IReadOnlyList<SearchHit>> FullTextSearchAsync(string query, int limit, CancellationToken ct)
    {
        Interlocked.Increment(ref TextCalls);
        var terms = TextFolding.ContentTerms(query).ToHashSet();
        IReadOnlyList<SearchHit> r = _rows
            .Select(r => r.Hit with { Score = TextFolding.ContentTerms(r.Hit.Text).Count(terms.Contains) })
            .Where(h => h.Score > 0)
            .OrderByDescending(h => h.Score).Take(limit).ToList();
        return Task.FromResult(r);
    }
}

internal sealed class InMemoryHistory : IChatHistoryStore
{
    public readonly ConcurrentDictionary<string, List<ChatTurn>> Turns = new();
    public bool FailOnAppend { get; set; }

    public Task<IReadOnlyList<ChatTurn>> GetRecentAsync(string userId, string sessionId, int maxTurns, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ChatTurn>>(Turns.TryGetValue($"{userId}/{sessionId}", out var l) ? l.TakeLast(maxTurns).ToList() : []);

    public Task AppendAsync(string userId, string sessionId, ChatTurn turn, CancellationToken ct)
    {
        if (FailOnAppend) throw new InvalidOperationException("mongo yok");
        Turns.GetOrAdd($"{userId}/{sessionId}", _ => []).Add(turn);
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryAudit : IAuditLog
{
    public readonly List<AuditEntry> Entries = [];

    public Task WriteAsync(AuditEntry entry, CancellationToken ct)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryAnswerCache : IAnswerCache
{
    public readonly Dictionary<string, CachedAnswer> Items = [];

    public Task<CachedAnswer?> GetAsync(string key, CancellationToken ct) => Task.FromResult(Items.GetValueOrDefault(key));

    public Task SetAsync(string key, CachedAnswer answer, CancellationToken ct)
    {
        Items[key] = answer;
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryDocuments : IDocumentRepository
{
    public readonly Dictionary<Guid, DocumentRecord> Docs = [];
    public readonly Dictionary<Guid, IReadOnlyList<IndexedChunk>> Chunks = [];

    public Task<Guid> CreateAsync(string title, string content, string createdBy, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        Docs[id] = new DocumentRecord(id, title, content, DocumentStatus.Queued, 0, null, null, DateTimeOffset.UtcNow, null);
        return Task.FromResult(id);
    }

    public Task<DocumentRecord?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Docs.GetValueOrDefault(id));

    public Task<IReadOnlyList<DocumentSummary>> ListAsync(int limit, CancellationToken ct) => throw new NotSupportedException();

    public Task MarkProcessingAsync(Guid id, CancellationToken ct)
    {
        Docs[id] = Docs[id] with { Status = DocumentStatus.Processing };
        return Task.CompletedTask;
    }

    public Task ReplaceChunksAsync(Guid id, string model, IReadOnlyList<IndexedChunk> chunks, CancellationToken ct)
    {
        Chunks[id] = chunks;
        Docs[id] = Docs[id] with { Status = DocumentStatus.Indexed, ChunkCount = chunks.Count, EmbeddingModel = model };
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(Guid id, string error, CancellationToken ct)
    {
        Docs[id] = Docs[id] with { Status = DocumentStatus.Failed, Error = error };
        return Task.CompletedTask;
    }
}

/// <summary>Gerçek SK Kernel + Extractive model + bellek içi altyapı ile orkestratör kurar.</summary>
internal sealed class ChatHarness
{
    public readonly HashingEmbeddingService Embeddings = new(256);
    public readonly InMemorySearchIndex Index;
    public readonly InMemoryHistory History = new();
    public readonly InMemoryAudit Audit = new();
    public readonly InMemoryAnswerCache Cache = new();
    public readonly TokenBudgetOptions Budget;

    public ChatHarness(TokenBudgetOptions? budget = null)
    {
        Index = new InMemorySearchIndex(Embeddings);
        Budget = budget ?? new TokenBudgetOptions();
    }

    public RagChatOrchestrator Build(IChatCompletionService? chat = null)
    {
        var kb = Kernel.CreateBuilder();
        kb.Services.AddSingleton(chat ?? new ExtractiveChatCompletionService());
        Kernel NewKernel() => kb.Build();

        var options = new AgentOptions();
        var search = new HybridSearchService(Index, Embeddings, new LexicalReranker(), new HybridSearchOptions());
        var tokens = new TiktokenTokenCounter();
        return new RagChatOrchestrator(
            new RetrieverAgent(NewKernel(), new KnowledgeBasePlugin(search), options),
            new AnswerAgent(NewKernel(), options, Budget),
            new VerifierAgent(NewKernel(), new GroundingScorer(), options),
            new ContextBudgeter(tokens, Budget),
            History, Audit, Cache,
            new ChatModelInfo(ExtractiveChatCompletionService.ModelId, "Extractive"),
            NullLogger<RagChatOrchestrator>.Instance);
    }

    public static async Task<List<ChatStreamEvent>> Collect(IAsyncEnumerable<ChatStreamEvent> stream)
    {
        var list = new List<ChatStreamEvent>();
        await foreach (var e in stream) list.Add(e);
        return list;
    }
}
