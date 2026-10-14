using System.Security.Claims;
using BelgeRag.Api.Auth;
using BelgeRag.Api.Contracts;
using BelgeRag.Core.Ingestion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BelgeRag.Api.Controllers;

[ApiController]
[Route("api/documents")]
[Authorize]
public sealed class DocumentsController(
    IDocumentRepository documents,
    IIngestQueue queue,
    ILogger<DocumentsController> logger) : ControllerBase
{
    private static readonly string[] AllowedExtensions = [".md", ".txt"];
    public const long MaxUploadBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Belgeyi kaydeder ve indeksleme için kuyruğa yazar. İndeksleme worker servisinde asenkron yapılır;
    /// yanıt 202 Accepted ve durum adresidir.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = Roles.Admin)]
    [ProducesResponseType<DocumentAccepted>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Create(CreateDocumentRequest request, CancellationToken ct)
    {
        var (accepted, error) = await AcceptAsync(request.Title.Trim(), request.Content, ct);
        return error ?? Accepted(accepted!.StatusUrl, accepted);
    }

    /// <summary>Bir veya daha fazla .md/.txt dosyasını yükler (dosya başına en fazla 2 MB).</summary>
    [HttpPost("upload")]
    [Authorize(Roles = Roles.Admin)]
    [RequestSizeLimit(20 * 1024 * 1024)]
    [ProducesResponseType<List<DocumentAccepted>>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Upload(IFormFileCollection files, CancellationToken ct)
    {
        if (files.Count == 0) return ValidationProblem("En az bir dosya gerekli.");
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext)) return ValidationProblem($"{f.FileName}: yalnızca .md ve .txt kabul edilir.");
            if (f.Length is 0 or > MaxUploadBytes) return ValidationProblem($"{f.FileName}: boş ya da 2 MB üzerinde.");
        }

        var list = new List<DocumentAccepted>();
        foreach (var f in files)
        {
            using var reader = new StreamReader(f.OpenReadStream());
            var content = await reader.ReadToEndAsync(ct);
            var (accepted, error) = await AcceptAsync(Path.GetFileNameWithoutExtension(f.FileName), content, ct);
            if (error is not null) return error;
            list.Add(accepted!);
        }
        return Accepted(list);
    }

    /// <summary>Son belgeleri durumlarıyla listeler.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<DocumentSummary>> List([FromQuery] int limit = 50, CancellationToken ct = default) =>
        await documents.ListAsync(Math.Clamp(limit, 1, 500), ct);

    /// <summary>Belgenin indeksleme durumu (queued → processing → indexed | failed).</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<DocumentSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentSummary>> Get(Guid id, CancellationToken ct)
    {
        var d = await documents.GetAsync(id, ct);
        if (d is null) return NotFound();
        return new DocumentSummary(d.Id, d.Title, d.Status, d.ChunkCount, d.Content.Length, d.EmbeddingModel, d.Error,
            d.CreatedAt, d.IndexedAt);
    }

    private async Task<(DocumentAccepted? Accepted, IActionResult? Error)> AcceptAsync(string title, string content, CancellationToken ct)
    {
        var user = User.FindFirstValue(ClaimTypes.Name) ?? "unknown";
        var id = await documents.CreateAsync(title, content, user, ct);
        try
        {
            await queue.EnqueueAsync(new IngestMessage(id, user, DateTimeOffset.UtcNow), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Belge {DocumentId} kuyruğa yazılamadı", id);
            await documents.MarkFailedAsync(id, "kuyruğa yazılamadı", CancellationToken.None);
            return (null, Problem("İndeksleme kuyruğu şu an erişilemez.", statusCode: StatusCodes.Status503ServiceUnavailable));
        }
        return (new DocumentAccepted(id, DocumentStatus.Queued, $"/api/documents/{id}"), null);
    }
}
