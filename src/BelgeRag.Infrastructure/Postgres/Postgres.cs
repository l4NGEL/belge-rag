using BelgeRag.Core.Ingestion;
using BelgeRag.Core.Search;
using BelgeRag.Core.Text;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace BelgeRag.Infrastructure.Postgres;

/// <summary>Şema ve indeksler. Embedding boyutu yapılandırmadan gelir; mevcut tabloyla uyuşmazsa açıkça hata verir.</summary>
public sealed class PgSchema(NpgsqlDataSource dataSource, int dimensions)
{
    public async Task EnsureAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var ext = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector", conn))
            await ext.ExecuteNonQueryAsync(ct);
        await conn.ReloadTypesAsync(ct);

        // tsvector 'simple' yapılandırmasıyla, Türkçe harfleri C# tarafında katlanmış (TextFolding) search_text
        // üzerinden üretilir; sorgu da aynı katlamayla ve önek eşleşmesiyle (term:*) kurulur. Böylece ekli
        // biçimler ("modelleri") kök önekle ("modell:*") eşleşir.
        var sql = $"""
            CREATE TABLE IF NOT EXISTS documents (
                id uuid PRIMARY KEY,
                title text NOT NULL,
                content text NOT NULL,
                status text NOT NULL,
                chunk_count int NOT NULL DEFAULT 0,
                embedding_model text,
                error text,
                created_by text NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                indexed_at timestamptz
            );
            CREATE TABLE IF NOT EXISTS chunks (
                id uuid PRIMARY KEY,
                document_id uuid NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
                ordinal int NOT NULL,
                content text NOT NULL,
                search_text text NOT NULL,
                token_count int NOT NULL,
                embedding_model text NOT NULL,
                embedding vector({dimensions}) NOT NULL,
                tsv tsvector GENERATED ALWAYS AS (to_tsvector('simple', search_text)) STORED
            );
            CREATE INDEX IF NOT EXISTS ix_chunks_document ON chunks (document_id);
            CREATE INDEX IF NOT EXISTS ix_chunks_tsv ON chunks USING gin (tsv);
            CREATE INDEX IF NOT EXISTS ix_chunks_embedding ON chunks USING hnsw (embedding vector_cosine_ops);
            """;
        await using (var cmd = new NpgsqlCommand(sql, conn))
            await cmd.ExecuteNonQueryAsync(ct);

        await using var check = new NpgsqlCommand(
            "SELECT atttypmod FROM pg_attribute WHERE attrelid = 'chunks'::regclass AND attname = 'embedding'", conn);
        var existing = Convert.ToInt32(await check.ExecuteScalarAsync(ct));
        if (existing != dimensions)
            throw new InvalidOperationException(
                $"chunks.embedding vector({existing}) ama yapılandırılan embedding boyutu {dimensions}. " +
                "Embedding modelini değiştirdiyseniz veritabanını sıfırlayın (docker compose down -v).");
    }
}

/// <summary>Scoped: her istek/mesaj kendi bağlantılarını havuzdan (NpgsqlDataSource, Singleton) alır.</summary>
public sealed class PgDocumentRepository(NpgsqlDataSource dataSource) : IDocumentRepository
{
    public async Task<Guid> CreateAsync(string title, string content, string createdBy, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO documents (id, title, content, status, created_by) VALUES ($1, $2, $3, $4, $5)");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(title);
        cmd.Parameters.AddWithValue(content);
        cmd.Parameters.AddWithValue(DocumentStatus.Queued);
        cmd.Parameters.AddWithValue(createdBy);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<DocumentRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT id, title, content, status, chunk_count, embedding_model, error, created_at, indexed_at FROM documents WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new DocumentRecord(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
            r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
            r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8));
    }

    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(int limit, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            SELECT id, title, status, chunk_count, length(content), embedding_model, error, created_at, indexed_at
            FROM documents ORDER BY created_at DESC LIMIT $1
            """);
        cmd.Parameters.AddWithValue(limit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<DocumentSummary>();
        while (await r.ReadAsync(ct))
            list.Add(new DocumentSummary(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8)));
        return list;
    }

    public Task MarkProcessingAsync(Guid id, CancellationToken ct) =>
        SetStatusAsync(id, DocumentStatus.Processing, null, ct);

    public Task MarkFailedAsync(Guid id, string error, CancellationToken ct) =>
        SetStatusAsync(id, DocumentStatus.Failed, error, ct);

    private async Task SetStatusAsync(Guid id, string status, string? error, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("UPDATE documents SET status = $2, error = $3 WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(status);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)error ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ReplaceChunksAsync(Guid id, string embeddingModel, IReadOnlyList<IndexedChunk> chunks, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        string title;
        await using (var t = new NpgsqlCommand("SELECT title FROM documents WHERE id = $1 FOR UPDATE", conn, tx))
        {
            t.Parameters.AddWithValue(id);
            title = (string)(await t.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException($"Belge {id} yok"));
        }
        await using (var del = new NpgsqlCommand("DELETE FROM chunks WHERE document_id = $1", conn, tx))
        {
            del.Parameters.AddWithValue(id);
            await del.ExecuteNonQueryAsync(ct);
        }

        // COPY BINARY: satır satır INSERT'e göre toplu yazımda belirgin şekilde hızlı.
        await using (var writer = await conn.BeginBinaryImportAsync(
            "COPY chunks (id, document_id, ordinal, content, search_text, token_count, embedding_model, embedding) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var c in chunks)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(c.Id, NpgsqlDbType.Uuid, ct);
                await writer.WriteAsync(id, NpgsqlDbType.Uuid, ct);
                await writer.WriteAsync(c.Ordinal, NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(c.Text, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(TextFolding.Fold($"{title}\n{c.Text}"), NpgsqlDbType.Text, ct);
                await writer.WriteAsync(c.TokenCount, NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(embeddingModel, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(new Vector(c.Embedding), ct);
            }
            await writer.CompleteAsync(ct);
        }

        await using (var upd = new NpgsqlCommand(
            """
            UPDATE documents SET status = $2, chunk_count = $3, embedding_model = $4, error = NULL, indexed_at = now()
            WHERE id = $1
            """, conn, tx))
        {
            upd.Parameters.AddWithValue(id);
            upd.Parameters.AddWithValue(DocumentStatus.Indexed);
            upd.Parameters.AddWithValue(chunks.Count);
            upd.Parameters.AddWithValue(embeddingModel);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}

public sealed class PgSearchIndex(NpgsqlDataSource dataSource) : ISearchIndex
{
    public async Task<IReadOnlyList<SearchHit>> VectorSearchAsync(float[] queryEmbedding, string embeddingModel, int limit, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            SELECT c.id, c.document_id, d.title, c.ordinal, c.content, 1 - (c.embedding <=> $1) AS score
            FROM chunks c JOIN documents d ON d.id = c.document_id
            WHERE c.embedding_model = $2
            ORDER BY c.embedding <=> $1
            LIMIT $3
            """);
        cmd.Parameters.AddWithValue(new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue(embeddingModel);
        cmd.Parameters.AddWithValue(limit);
        return await ReadHitsAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<SearchHit>> FullTextSearchAsync(string query, int limit, CancellationToken ct)
    {
        var tsQuery = BuildTsQuery(query);
        if (tsQuery.Length == 0) return [];
        await using var cmd = dataSource.CreateCommand(
            """
            SELECT c.id, c.document_id, d.title, c.ordinal, c.content, ts_rank_cd(c.tsv, q, 32)::float8 AS score
            FROM chunks c JOIN documents d ON d.id = c.document_id, to_tsquery('simple', $1) q
            WHERE c.tsv @@ q
            ORDER BY score DESC
            LIMIT $2
            """);
        cmd.Parameters.AddWithValue(tsQuery);
        cmd.Parameters.AddWithValue(limit);
        return await ReadHitsAsync(cmd, ct);
    }

    /// <summary>
    /// Kullanıcı girdisinden güvenli tsquery: yalnızca harf/rakamdan oluşan kök önekleri, OR ile, önek eşleşmeli.
    /// to_tsquery sözdizimi karakterleri (&amp;, |, !, :, parantez) bu şekilde hiç sorguya ulaşmaz.
    /// </summary>
    public static string BuildTsQuery(string query) =>
        string.Join(" | ", TextFolding.ContentTerms(query).Distinct().Select(t => $"{t}:*"));

    private static async Task<IReadOnlyList<SearchHit>> ReadHitsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var hits = new List<SearchHit>();
        while (await r.ReadAsync(ct))
            hits.Add(new SearchHit(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetDouble(5)));
        return hits;
    }
}
