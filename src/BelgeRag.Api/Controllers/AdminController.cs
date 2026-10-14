using BelgeRag.Api.Auth;
using BelgeRag.Infrastructure.Mongo;
using BelgeRag.Infrastructure.Redis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BelgeRag.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = Roles.Admin)]
public sealed class AdminController(CacheStats stats, MongoAuditLog audit) : ControllerBase
{
    /// <summary>Bu API sürecinin Redis önbellek isabet sayaçları.</summary>
    [HttpGet("cache-stats")]
    public object CacheStats() => stats.Snapshot();

    /// <summary>Son sohbet audit kayıtları (MongoDB).</summary>
    [HttpGet("audit")]
    public Task<List<AuditDocument>> Audit([FromQuery] int limit = 50, CancellationToken ct = default) =>
        audit.RecentAsync(Math.Clamp(limit, 1, 500), ct);
}
