using System.Diagnostics;
using BelgeRag.Core.Embeddings;
using BelgeRag.Core.Text;
using Microsoft.Extensions.Logging;

namespace BelgeRag.Core.Ingestion;

public static class DocumentStatus
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Indexed = "indexed";
    public const string Failed = "failed";
}

public sealed record DocumentRecord(
    Guid Id,
    string Title,
    string Content,
    string Status,
    int ChunkCount,
    string? EmbeddingModel,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? IndexedAt);

public sealed record DocumentSummary(
    Guid Id,
    string Title,
    string Status,
    int ChunkCount,
    int ContentLength,
    string? EmbeddingModel,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? IndexedAt);

public sealed record IndexedChunk(Guid Id, int Ordinal, string Text, int TokenCount, float[] Embedding);

public interface IDocumentRepository
{
    Task<Guid> CreateAsync(string title, string content, string createdBy, CancellationToken ct);

    Task<DocumentRecord?> GetAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<DocumentSummary>> ListAsync(int limit, CancellationToken ct);

    Task MarkProcessingAsync(Guid id, CancellationToken ct);

    /// <summary>Belgenin tüm chunk'larını tek transaction'da değiştirir (yeniden teslimde idempotent).</summary>
    Task ReplaceChunksAsync(Guid id, string embeddingModel, IReadOnlyList<IndexedChunk> chunks, CancellationToken ct);

    Task MarkFailedAsync(Guid id, string error, CancellationToken ct);
}

/// <summary>Kuyruğa giden mesaj yalnızca kimlik taşır; içerik PostgreSQL'de durur (mesajlar küçük kalır).</summary>
public sealed record IngestMessage(Guid DocumentId, string RequestedBy, DateTimeOffset RequestedAt);

public interface IIngestQueue
{
    Task EnqueueAsync(IngestMessage message, CancellationToken ct);
}

public sealed record IngestOutcome(Guid DocumentId, int Chunks, int Tokens, double ChunkMs, double EmbedMs, double WriteMs);

/// <summary>Worker tarafında, her kuyruk mesajı için ayrı bir DI kapsamında çalışır (Scoped).</summary>
public sealed class DocumentIngestionService(
    IDocumentRepository documents,
    TextChunker chunker,
    IEmbeddingService embeddings,
    ChunkingOptions chunking,
    ILogger<DocumentIngestionService> logger)
{
    public const int EmbedBatchSize = 32;

    public async Task<IngestOutcome?> IngestAsync(Guid documentId, CancellationToken ct)
    {
        var doc = await documents.GetAsync(documentId, ct);
        if (doc is null)
        {
            logger.LogWarning("Belge {DocumentId} bulunamadı; mesaj atlanıyor", documentId);
            return null;
        }
        await documents.MarkProcessingAsync(documentId, ct);

        var sw = Stopwatch.StartNew();
        var chunks = chunker.Chunk(doc.Content, chunking);
        var chunkMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var vectors = new List<float[]>(chunks.Count);
        foreach (var batch in chunks.Chunk(EmbedBatchSize))
            vectors.AddRange(await embeddings.EmbedAsync(batch.Select(c => $"{doc.Title}\n{c.Text}").ToList(), EmbeddingPurpose.Document, ct));
        var embedMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var indexed = chunks.Select((c, i) => new IndexedChunk(Guid.NewGuid(), c.Ordinal, c.Text, c.TokenCount, vectors[i])).ToList();
        await documents.ReplaceChunksAsync(documentId, embeddings.ModelId, indexed, ct);
        var writeMs = sw.Elapsed.TotalMilliseconds;

        var outcome = new IngestOutcome(documentId, indexed.Count, indexed.Sum(c => c.TokenCount),
            Math.Round(chunkMs, 2), Math.Round(embedMs, 2), Math.Round(writeMs, 2));
        logger.LogInformation("Belge {DocumentId} indekslendi: {Chunks} parça, {Tokens} token, embed {EmbedMs} ms",
            documentId, outcome.Chunks, outcome.Tokens, outcome.EmbedMs);
        return outcome;
    }
}
