using BelgeRag.Api.Contracts;
using BelgeRag.Core.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BelgeRag.Api.Controllers;

[ApiController]
[Route("api/search")]
[Authorize]
public sealed class SearchController(HybridSearchService search) : ControllerBase
{
    /// <summary>Hibrit (vektör + tam metin, RRF) arama ve isteğe bağlı rerank. mode ile tek yöntem de seçilebilir.</summary>
    [HttpGet]
    public Task<SearchResult> Get([FromQuery] SearchQuery query, CancellationToken ct) =>
        search.SearchAsync(new SearchRequest(query.Q, query.TopK, query.Mode, query.Rerank), ct);
}
