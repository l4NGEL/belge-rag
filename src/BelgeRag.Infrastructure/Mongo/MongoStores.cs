using BelgeRag.Core.Chat;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace BelgeRag.Infrastructure.Mongo;

public sealed class ChatTurnDocument
{
    [BsonId] public ObjectId Id { get; set; }
    public string UserId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public DateTime At { get; set; }
}

public sealed class AuditDocument
{
    [BsonId] public ObjectId Id { get; set; }
    public string UserId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Question { get; set; } = "";
    public string StandaloneQuery { get; set; } = "";
    public List<string> ChunkIds { get; set; } = [];
    public string Verdict { get; set; } = "";
    public double SupportedSentenceRatio { get; set; }
    public string ChatModel { get; set; } = "";
    public double RetrievalMs { get; set; }
    public double FirstTokenMs { get; set; }
    public double TotalMs { get; set; }
    public int PromptTokens { get; set; }
    public bool AnswerFromCache { get; set; }
    public DateTime At { get; set; }
}

/// <summary>Koleksiyon tutamaçları ve indeksler. IMongoClient iş parçacığı güvenli ve bağlantı havuzu tuttuğu için Singleton.</summary>
public sealed class MongoContext(IMongoClient client, MongoOptions options)
{
    private readonly IMongoDatabase _db = client.GetDatabase(options.Database);

    public IMongoCollection<ChatTurnDocument> Turns => _db.GetCollection<ChatTurnDocument>("chat_turns");
    public IMongoCollection<AuditDocument> Audit => _db.GetCollection<AuditDocument>("audit");

    public async Task EnsureIndexesAsync(CancellationToken ct)
    {
        await Turns.Indexes.CreateOneAsync(new CreateIndexModel<ChatTurnDocument>(
            Builders<ChatTurnDocument>.IndexKeys.Ascending(t => t.UserId).Ascending(t => t.SessionId).Descending(t => t.At)),
            cancellationToken: ct);
        await Audit.Indexes.CreateOneAsync(new CreateIndexModel<AuditDocument>(
            Builders<AuditDocument>.IndexKeys.Descending(a => a.At)), cancellationToken: ct);
        await Audit.Indexes.CreateOneAsync(new CreateIndexModel<AuditDocument>(
            Builders<AuditDocument>.IndexKeys.Ascending(a => a.UserId).Descending(a => a.At)), cancellationToken: ct);
    }

    public async Task PingAsync(CancellationToken ct) =>
        await _db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
}

public sealed class MongoChatHistoryStore(MongoContext ctx) : IChatHistoryStore
{
    public async Task<IReadOnlyList<ChatTurn>> GetRecentAsync(string userId, string sessionId, int maxTurns, CancellationToken ct)
    {
        // Oturum kimliği kullanıcıya bağlı sorgulanır: başka kullanıcının oturum kimliğini bilen geçmişini okuyamaz.
        var docs = await ctx.Turns.Find(t => t.UserId == userId && t.SessionId == sessionId)
            .SortByDescending(t => t.At).Limit(maxTurns).ToListAsync(ct);
        docs.Reverse();
        return docs.Select(d => new ChatTurn(d.Question, d.Answer, new DateTimeOffset(d.At, TimeSpan.Zero))).ToList();
    }

    public Task AppendAsync(string userId, string sessionId, ChatTurn turn, CancellationToken ct) =>
        ctx.Turns.InsertOneAsync(new ChatTurnDocument
        {
            UserId = userId,
            SessionId = sessionId,
            Question = turn.Question,
            Answer = turn.Answer,
            At = turn.At.UtcDateTime,
        }, cancellationToken: ct);
}

public sealed class MongoAuditLog(MongoContext ctx) : IAuditLog
{
    public Task WriteAsync(AuditEntry e, CancellationToken ct) =>
        ctx.Audit.InsertOneAsync(new AuditDocument
        {
            UserId = e.UserId,
            SessionId = e.SessionId,
            Question = e.Question,
            StandaloneQuery = e.StandaloneQuery,
            ChunkIds = e.ChunkIds.Select(c => c.ToString()).ToList(),
            Verdict = e.Verdict.ToString(),
            SupportedSentenceRatio = e.SupportedSentenceRatio,
            ChatModel = e.ChatModel,
            RetrievalMs = e.Metrics.RetrievalMs,
            FirstTokenMs = e.Metrics.FirstTokenMs,
            TotalMs = e.Metrics.TotalMs,
            PromptTokens = e.Metrics.PromptTokens,
            AnswerFromCache = e.Metrics.AnswerFromCache,
            At = e.At.UtcDateTime,
        }, cancellationToken: ct);

    public Task<List<AuditDocument>> RecentAsync(int limit, CancellationToken ct) =>
        ctx.Audit.Find(FilterDefinition<AuditDocument>.Empty).SortByDescending(a => a.At).Limit(limit).ToListAsync(ct);
}
