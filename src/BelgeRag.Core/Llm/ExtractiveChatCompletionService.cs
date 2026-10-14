using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using BelgeRag.Core.Prompting;
using BelgeRag.Core.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace BelgeRag.Core.Llm;

/// <summary>
/// LLM gerektirmeyen, Semantic Kernel <see cref="IChatCompletionService"/> uygulaması.
/// Prompt'taki numaralı kaynaklardan soruyla en çok terim örtüşen cümleleri seçer ve
/// kaynak numarasıyla birlikte sözcük sözcük akıtır. Üretken değildir; amacı API kredisi
/// ya da GPU olmadan tüm hattın (SSE, önbellek, doğrulayıcı, audit) çalışması ve testlerin
/// deterministik olmasıdır. Yapılandırmada Llm:Provider=Extractive ile seçilir.
/// </summary>
public sealed partial class ExtractiveChatCompletionService : IChatCompletionService
{
    public const string ModelId = "extractive-baseline";
    public const string NoAnswer = "Kaynaklarda bu soruya yanıt bulamadım.";

    public IReadOnlyDictionary<string, object?> Attributes { get; } =
        new Dictionary<string, object?> { ["ModelId"] = ModelId };

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessageContent> result =
            [new ChatMessageContent(AuthorRole.Assistant, Answer(chatHistory), ModelId)];
        return Task.FromResult(result);
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var answer = Answer(chatHistory);
        foreach (Match m in WordWithTrailingSpace().Matches(answer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, m.Value, modelId: ModelId);
        }
    }

    internal static string Answer(ChatHistory history)
    {
        var last = history.LastOrDefault(m => m.Role == AuthorRole.User)?.Content ?? string.Empty;
        var (sources, question) = PromptBuilder.Parse(last);
        var qTerms = TextFolding.ContentTerms(question).ToHashSet();
        if (sources.Count == 0 || qTerms.Count == 0) return NoAnswer;

        var threshold = Math.Max(1, (int)Math.Ceiling(qTerms.Count * 0.34));
        var candidates = new List<(string Sentence, int Source, double Score, int Overlap)>();
        foreach (var (number, text) in sources)
        {
            foreach (var sentence in SentenceSplit().Split(text))
            {
                var s = sentence.Trim();
                if (s.Length < 12) continue;
                var terms = TextFolding.ContentTerms(s).ToList();
                var overlap = qTerms.Count(terms.Contains);
                if (overlap < threshold) continue;
                // Uzun cümleleri hafifçe cezalandır; kaynak sırası (rerank) eşitlikte öne geçsin.
                var score = overlap / Math.Sqrt(1 + terms.Count / 12.0) - number * 0.01;
                candidates.Add((s, number, score, overlap));
            }
        }
        if (candidates.Count == 0) return NoAnswer;

        var picked = candidates.OrderByDescending(c => c.Score).Take(2).ToList();
        var sb = new StringBuilder();
        foreach (var (sentence, source, _, _) in picked)
        {
            var clean = sentence.TrimEnd('.', ' ');
            sb.Append(clean).Append(" [").Append(source).Append("]. ");
        }
        return sb.ToString().TrimEnd();
    }

    [GeneratedRegex(@"\S+\s*")]
    private static partial Regex WordWithTrailingSpace();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceSplit();
}
