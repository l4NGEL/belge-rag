using System.Security.Claims;
using BelgeRag.Api.Contracts;
using BelgeRag.Api.Sse;
using BelgeRag.Core.Chat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BelgeRag.Api.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize]
public sealed class ChatController(
    RagChatOrchestrator orchestrator,
    IChatHistoryStore history,
    ILogger<ChatController> logger) : ControllerBase
{
    /// <summary>
    /// Soruyu yanıtlar ve sonucu Server-Sent Events olarak akıtır. Olay sırası:
    /// sources → token* → verdict → done (hata halinde error).
    /// </summary>
    [HttpPost("stream")]
    [EnableRateLimiting("chat")]
    [Produces("text/event-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task Stream(ChatStreamRequest request, CancellationToken ct)
    {
        SseWriter.Prepare(Response);
        var userId = User.FindFirstValue(ClaimTypes.Name) ?? "unknown";
        try
        {
            await foreach (var evt in orchestrator.StreamAsync(userId, new ChatRequest(request.Question, request.SessionId, request.TopK), ct))
                await SseWriter.WriteAsync(Response, evt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("İstemci akışı kapattı");
        }
        catch (Exception e)
        {
            // Başlıklar gönderildiği için durum kodu artık değiştirilemez; hatayı olay olarak bildir.
            logger.LogError(e, "Sohbet akışı başarısız");
            await SseWriter.WriteAsync(Response, new ErrorEvent("Yanıt üretilemedi."), CancellationToken.None);
        }
    }

    /// <summary>Oturumun sohbet geçmişi (MongoDB). Yalnızca oturumun sahibi okuyabilir.</summary>
    [HttpGet("sessions/{sessionId}")]
    public Task<IReadOnlyList<ChatTurn>> Session(string sessionId, CancellationToken ct) =>
        history.GetRecentAsync(User.FindFirstValue(ClaimTypes.Name) ?? "unknown", sessionId, 50, ct);
}
