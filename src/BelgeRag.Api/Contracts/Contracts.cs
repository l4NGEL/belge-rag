using System.ComponentModel.DataAnnotations;
using BelgeRag.Core.Search;

namespace BelgeRag.Api.Contracts;

public sealed record LoginRequest([Required] string Username, [Required] string Password);

public sealed record CreateDocumentRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Title,
    [Required, StringLength(2_000_000, MinimumLength = 1)] string Content);

public sealed record DocumentAccepted(Guid Id, string Status, string StatusUrl);

public sealed record ChatStreamRequest(
    [Required, StringLength(2000, MinimumLength = 2)] string Question,
    [StringLength(64)] string? SessionId = null,
    [Range(1, 10)] int TopK = 5);

public sealed record SearchQuery
{
    /// <summary>Arama metni.</summary>
    [Required, StringLength(500, MinimumLength = 2)]
    public string Q { get; init; } = "";

    [Range(1, 20)]
    public int TopK { get; init; } = 5;

    public SearchMode Mode { get; init; } = SearchMode.Hybrid;

    public bool Rerank { get; init; } = true;
}
