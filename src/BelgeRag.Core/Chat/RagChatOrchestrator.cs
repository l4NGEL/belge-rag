using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BelgeRag.Core.Agents;
using BelgeRag.Core.Prompting;
using BelgeRag.Core.Text;
using Microsoft.Extensions.Logging;

namespace BelgeRag.Core.Chat;

public sealed record ChatModelInfo(string ChatModel, string Provider);

/// <summary>
/// Çok ajanlı RAG akışı: Retriever → (bütçe) → Cevaplayıcı (akış) → Doğrulayıcı.
/// Sonucu <see cref="IAsyncEnumerable{T}"/> olarak verir; API katmanı her olayı
/// gelir gelmez SSE çerçevesi olarak yazar, yani ilk token kullanıcıya yanıtın
/// tamamı üretilmeden ulaşır.
/// </summary>
public sealed partial class RagChatOrchestrator(
    RetrieverAgent retriever,
    AnswerAgent answerer,
    VerifierAgent verifier,
    ContextBudgeter budgeter,
    IChatHistoryStore history,
    IAuditLog audit,
    IAnswerCache cache,
    ChatModelInfo model,
    ILogger<RagChatOrchestrator> logger)
{
    public const int HistoryTurnsToLoad = 10;

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        string userId, ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var sessionId = string.IsNullOrWhiteSpace(request.SessionId) ? Guid.NewGuid().ToString("N") : request.SessionId;
        var past = await history.GetRecentAsync(userId, sessionId, HistoryTurnsToLoad, ct);

        var (standalone, search) = await retriever.RetrieveAsync(request.Question, past, request.TopK, ct);
        var retrievalMs = total.Elapsed.TotalMilliseconds;

        var ctx = budgeter.Fit(PromptBuilder.AnswerInstructions, request.Question, search.Hits, past);
        yield return new SourcesEvent(sessionId, SourceRefs.From(ctx.Sources));

        // Yanıt önbelleği yalnızca ilk turda: geçmiş prompt'a girdiğinde aynı soru farklı yanıt gerektirebilir.
        var cacheKey = past.Count == 0 ? AnswerCacheKey(model.ChatModel, standalone, ctx.Sources.Select(s => s.ChunkId)) : null;
        var cached = cacheKey is null ? null : await cache.GetAsync(cacheKey, ct);

        var answer = new StringBuilder();
        double firstTokenMs = -1;
        Verdict verdict;
        if (cached is not null)
        {
            foreach (Match m in Word().Matches(cached.Answer))
            {
                if (firstTokenMs < 0) firstTokenMs = total.Elapsed.TotalMilliseconds;
                answer.Append(m.Value);
                yield return new TokenEvent(m.Value);
            }
            verdict = cached.Verdict;
        }
        else
        {
            await foreach (var token in answerer.StreamAsync(request.Question, ctx.Sources, ctx.History, ct))
            {
                if (firstTokenMs < 0) firstTokenMs = total.Elapsed.TotalMilliseconds;
                answer.Append(token);
                yield return new TokenEvent(token);
            }
            verdict = await verifier.VerifyAsync(request.Question, answer.ToString(), ctx.Sources, ct);
            if (cacheKey is not null && verdict.Label is VerdictLabel.Grounded or VerdictLabel.Partial)
                await cache.SetAsync(cacheKey, new CachedAnswer(answer.ToString(), verdict), ct);
        }
        yield return new VerdictEvent(verdict);

        var metrics = new ChatMetrics(
            Math.Round(retrievalMs, 2), Math.Round(firstTokenMs, 2), Math.Round(total.Elapsed.TotalMilliseconds, 2),
            ctx.PromptTokens, budgeter.Options.ContextWindowTokens, ctx.Sources.Count, ctx.DroppedChunks,
            ctx.TruncatedChunks, ctx.History.Count, ctx.DroppedHistoryTurns, cached is not null, standalone);

        await PersistAsync(userId, sessionId, request.Question, standalone, answer.ToString(), ctx, verdict, metrics, ct);
        yield return new DoneEvent(metrics);
    }

    private async Task PersistAsync(string userId, string sessionId, string question, string standalone, string answer,
        BudgetedContext ctx, Verdict verdict, ChatMetrics metrics, CancellationToken ct)
    {
        // Geçmiş/audit yazımı başarısız olursa kullanıcı yanıtını zaten aldı; akışı bozma, logla.
        try
        {
            var now = DateTimeOffset.UtcNow;
            await history.AppendAsync(userId, sessionId, new ChatTurn(question, answer, now), ct);
            await audit.WriteAsync(new AuditEntry(userId, sessionId, question, standalone,
                ctx.Sources.Select(s => s.ChunkId).ToList(), verdict.Label, verdict.SupportedSentenceRatio,
                metrics, model.ChatModel, now), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Sohbet geçmişi/audit yazılamadı (oturum {SessionId})", sessionId);
        }
    }

    public static string AnswerCacheKey(string chatModel, string standaloneQuery, IEnumerable<Guid> chunkIds)
    {
        var material = $"{chatModel}|{TextFolding.Fold(standaloneQuery).Trim()}|{string.Join(',', chunkIds)}";
        return "answer:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    [GeneratedRegex(@"\S+\s*")]
    private static partial Regex Word();
}
