using BelgeRag.Core.Embeddings;
using Microsoft.Extensions.AI;

namespace BelgeRag.Infrastructure.Llm;

/// <summary>
/// Semantic Kernel'in kaydettiği <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> (OpenAI / Ollama)
/// üzerine ince uyarlayıcı. Modelin beklediği görev öneklerini (sorgu/belge) ekler ve
/// L2 normalize eder ki kosinüs ve iç çarpım aynı sıralamayı versin.
/// </summary>
public sealed class SemanticKernelEmbeddingService(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    string model,
    int dimensions,
    string queryPrefix = "",
    string documentPrefix = "") : IEmbeddingService
{
    /// <summary>Önek kullanımı vektör uzayını değiştirdiği için model kimliğine dahildir; eski vektörlerle karışmaz.</summary>
    public string ModelId { get; } = queryPrefix.Length + documentPrefix.Length > 0 ? $"{model}+prefix" : model;

    public int Dimensions { get; } = dimensions;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct = default)
    {
        var prefix = purpose == EmbeddingPurpose.Query ? queryPrefix : documentPrefix;
        var input = prefix.Length == 0 ? texts : texts.Select(t => prefix + t).ToList();
        var result = await generator.GenerateAsync(input, cancellationToken: ct);
        return result.Select(e =>
        {
            var v = e.Vector.ToArray();
            if (v.Length != Dimensions)
                throw new InvalidOperationException(
                    $"{ModelId} {v.Length} boyutlu vektör döndürdü; Embeddings:Dimensions={Dimensions} ayarlı.");
            var norm = MathF.Sqrt(v.Sum(x => x * x));
            if (norm > 0) for (var i = 0; i < v.Length; i++) v[i] /= norm;
            return v;
        }).ToList();
    }
}
