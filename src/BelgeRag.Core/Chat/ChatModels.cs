using System.Text.Json.Serialization;
using BelgeRag.Core.Search;

namespace BelgeRag.Core.Chat;

public sealed record ChatTurn(string Question, string Answer, DateTimeOffset At);

public sealed record ChatRequest(string Question, string? SessionId = null, int TopK = 5);

public sealed record SourceRef(int Number, Guid DocumentId, Guid ChunkId, string Title, int Ordinal, double Score, string Snippet);

public enum VerdictLabel
{
    Grounded,
    Partial,
    Ungrounded,
    NoAnswer,
}

public sealed record Verdict(
    VerdictLabel Label,
    double SupportedSentenceRatio,
    IReadOnlyList<int> CitedSources,
    IReadOnlyList<int> InvalidCitations,
    string Method,
    string? Note = null);

public sealed record ChatMetrics(
    double RetrievalMs,
    double FirstTokenMs,
    double TotalMs,
    int PromptTokens,
    int ContextWindowTokens,
    int SourcesUsed,
    int SourcesDropped,
    int SourcesTruncated,
    int HistoryTurnsUsed,
    int HistoryTurnsDropped,
    bool AnswerFromCache,
    string StandaloneQuery);

/// <summary>SSE ile akıtılan olaylar. Her biri ayrı bir "event:" adıyla yayınlanır.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SourcesEvent), "sources")]
[JsonDerivedType(typeof(TokenEvent), "token")]
[JsonDerivedType(typeof(VerdictEvent), "verdict")]
[JsonDerivedType(typeof(DoneEvent), "done")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
public abstract record ChatStreamEvent
{
    [JsonIgnore]
    public abstract string EventName { get; }
}

public sealed record SourcesEvent(string SessionId, IReadOnlyList<SourceRef> Sources) : ChatStreamEvent
{
    [JsonIgnore]
    public override string EventName => "sources";
}

public sealed record TokenEvent(string Text) : ChatStreamEvent
{
    [JsonIgnore]
    public override string EventName => "token";
}

public sealed record VerdictEvent(Verdict Verdict) : ChatStreamEvent
{
    [JsonIgnore]
    public override string EventName => "verdict";
}

public sealed record DoneEvent(ChatMetrics Metrics) : ChatStreamEvent
{
    [JsonIgnore]
    public override string EventName => "done";
}

public sealed record ErrorEvent(string Message) : ChatStreamEvent
{
    [JsonIgnore]
    public override string EventName => "error";
}

public interface IChatHistoryStore
{
    Task<IReadOnlyList<ChatTurn>> GetRecentAsync(string userId, string sessionId, int maxTurns, CancellationToken ct);

    Task AppendAsync(string userId, string sessionId, ChatTurn turn, CancellationToken ct);
}

public sealed record AuditEntry(
    string UserId,
    string SessionId,
    string Question,
    string StandaloneQuery,
    IReadOnlyList<Guid> ChunkIds,
    VerdictLabel Verdict,
    double SupportedSentenceRatio,
    ChatMetrics Metrics,
    string ChatModel,
    DateTimeOffset At);

public interface IAuditLog
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct);
}

public sealed record CachedAnswer(string Answer, Verdict Verdict);

public interface IAnswerCache
{
    Task<CachedAnswer?> GetAsync(string key, CancellationToken ct);

    Task SetAsync(string key, CachedAnswer answer, CancellationToken ct);
}

public static class SourceRefs
{
    public static IReadOnlyList<SourceRef> From(IReadOnlyList<SearchHit> hits) =>
        hits.Select((h, i) => new SourceRef(i + 1, h.DocumentId, h.ChunkId, h.DocumentTitle, h.Ordinal,
            Math.Round(h.Score, 4), h.Text.Length > 240 ? h.Text[..240] + "…" : h.Text)).ToList();
}
