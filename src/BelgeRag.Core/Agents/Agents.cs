using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using BelgeRag.Core.Chat;
using BelgeRag.Core.Llm;
using BelgeRag.Core.Prompting;
using BelgeRag.Core.Search;
using BelgeRag.Core.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

namespace BelgeRag.Core.Agents;

public sealed record AgentOptions
{
    /// <summary>Takip sorularını LLM ile bağımsız sorguya çevir (Extractive modda kapalı).</summary>
    public bool CondenseWithLlm { get; init; }

    /// <summary>Doğrulayıcı, sözcüksel dayanak kontrolüne ek olarak LLM'e de danışsın.</summary>
    public bool VerifyWithLlm { get; init; }

    public double Temperature { get; init; } = 0.1;
}

/// <summary>
/// Bilgi tabanını Semantic Kernel eklentisi olarak açar. Ajanlar aramayı bu fonksiyon
/// üzerinden çağırır; böylece SK fonksiyon filtreleri (loglama, telemetri) araya girebilir
/// ve fonksiyon çağırma destekleyen modellerde aynı araç LLM'e de verilebilir.
/// </summary>
public sealed class KnowledgeBasePlugin(HybridSearchService search)
{
    public const string PluginName = "kb";

    [KernelFunction("search_documents")]
    [Description("Belge tabanında hibrit (vektör + tam metin) arama yapar ve en ilgili parçaları döndürür.")]
    public Task<SearchResult> SearchAsync(
        [Description("Aranacak bağımsız sorgu")] string query,
        [Description("Döndürülecek parça sayısı")] int topK = 5,
        CancellationToken cancellationToken = default) =>
        search.SearchAsync(new SearchRequest(query, topK), cancellationToken);
}

/// <summary>Ajan 1 — Retriever: takip sorusunu bağımsızlaştırır, bilgi tabanını sorgular.</summary>
public sealed class RetrieverAgent(Kernel kernel, KnowledgeBasePlugin plugin, AgentOptions options)
{
    private const string CondenseInstructions =
        """
        Sohbet geçmişini ve son soruyu oku. Son soruyu, geçmiş olmadan da anlaşılacak tek bir arama sorgusuna dönüştür.
        Yalnızca sorguyu yaz; açıklama ekleme.
        """;

    public async Task<(string StandaloneQuery, SearchResult Result)> RetrieveAsync(
        string question, IReadOnlyList<ChatTurn> history, int topK, CancellationToken ct)
    {
        var standalone = await CondenseAsync(question, history, ct);
        if (!kernel.Plugins.Contains(KnowledgeBasePlugin.PluginName))
            kernel.ImportPluginFromObject(plugin, KnowledgeBasePlugin.PluginName);
        var result = await kernel.InvokeAsync<SearchResult>(KnowledgeBasePlugin.PluginName, "search_documents",
            new KernelArguments { ["query"] = standalone, ["topK"] = topK }, ct);
        return (standalone, result!);
    }

    internal async Task<string> CondenseAsync(string question, IReadOnlyList<ChatTurn> history, CancellationToken ct)
    {
        if (history.Count == 0) return question;
        if (!options.CondenseWithLlm)
        {
            // Sezgisel: kısa takip sorularına ("peki ya testleri?") önceki soruyu ekle.
            var terms = TextFolding.ContentTerms(question).Count();
            return terms < 4 ? $"{history[^1].Question} {question}" : question;
        }

        var agent = new ChatCompletionAgent
        {
            Name = "SorguYazici",
            Instructions = CondenseInstructions,
            Kernel = kernel,
            Arguments = new KernelArguments(Settings(0, 64)),
        };
        var transcript = string.Join("\n", history.TakeLast(3).Select(t => $"Kullanıcı: {t.Question}\nAsistan: {t.Answer}"));
        var message = new ChatMessageContent(AuthorRole.User, $"GEÇMİŞ:\n{transcript}\n\nSON SORU: {question}");
        await foreach (var item in agent.InvokeAsync([message], cancellationToken: ct))
        {
            var text = item.Message.Content?.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return question;
    }

    internal static PromptExecutionSettings Settings(double temperature, int maxTokens) => new()
    {
        ExtensionData = new Dictionary<string, object> { ["temperature"] = temperature, ["max_tokens"] = maxTokens },
    };
}

/// <summary>Ajan 2 — Cevaplayıcı: kaynaklara dayalı yanıtı token token üretir.</summary>
public sealed class AnswerAgent(Kernel kernel, AgentOptions options, TokenBudgetOptions budget)
{
    public async IAsyncEnumerable<string> StreamAsync(
        string question, IReadOnlyList<SearchHit> sources, IReadOnlyList<ChatTurn> history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var agent = new ChatCompletionAgent
        {
            Name = "Cevaplayici",
            Instructions = PromptBuilder.AnswerInstructions,
            Kernel = kernel,
            Arguments = new KernelArguments(RetrieverAgent.Settings(options.Temperature, budget.ReservedForAnswer)),
        };

        var messages = new List<ChatMessageContent>();
        foreach (var turn in history)
        {
            messages.Add(new ChatMessageContent(AuthorRole.User, turn.Question));
            messages.Add(new ChatMessageContent(AuthorRole.Assistant, turn.Answer));
        }
        messages.Add(new ChatMessageContent(AuthorRole.User, PromptBuilder.BuildUserMessage(question, sources)));

        await foreach (var item in agent.InvokeStreamingAsync(messages, cancellationToken: ct))
        {
            if (!string.IsNullOrEmpty(item.Message.Content))
                yield return item.Message.Content;
        }
    }
}

/// <summary>
/// Yanıttaki cümlelerin kaynak metinlerde dayanağı olup olmadığını sözcüksel olarak ölçer.
/// Anlamsal çıkarım (NLI) değildir: başka kelimelerle doğru söylenmiş bir cümleyi
/// "desteksiz" sayabilir; kaynaktaki kelimelerle kurulmuş yanlış bir cümleyi kaçırabilir.
/// </summary>
public sealed partial class GroundingScorer
{
    public const double SentenceSupportThreshold = 0.6;

    public Verdict Score(string answer, IReadOnlyList<SearchHit> sources)
    {
        if (string.IsNullOrWhiteSpace(answer) || sources.Count == 0 || IsRefusal(answer))
            return new Verdict(VerdictLabel.NoAnswer, 0, [], [], "lexical");

        // "[1]", "[1, 2]", "[1], [3]" biçimlerinin hepsi.
        var cited = Citation().Matches(answer)
            .SelectMany(m => m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(int.Parse).Distinct().Order().ToList();
        var invalid = cited.Where(n => n < 1 || n > sources.Count).ToList();
        var sourceTerms = sources.SelectMany(s => TextFolding.ContentTerms(s.Text)).ToHashSet();

        int total = 0, supported = 0;
        foreach (var raw in SentenceSplit().Split(Citation().Replace(answer, " ")))
        {
            var terms = TextFolding.ContentTerms(raw).Distinct().ToList();
            if (terms.Count < 2) continue;
            total++;
            if ((double)terms.Count(sourceTerms.Contains) / terms.Count >= SentenceSupportThreshold) supported++;
        }
        var ratio = total == 0 ? 0 : Math.Round((double)supported / total, 3);
        var label = ratio >= 0.8 && invalid.Count == 0 && cited.Count > 0 ? VerdictLabel.Grounded
            : ratio >= 0.5 ? VerdictLabel.Partial
            : VerdictLabel.Ungrounded;
        return new Verdict(label, ratio, cited, invalid, "lexical");
    }

    /// <summary>
    /// Modelin "kaynaklarda bilgi yok" yanıtı. Bu bir dayanaklı iddia değildir; Grounded sayılırsa
    /// doğrulayıcı istatistiği şişer. Kalıplar Türkçe ve İngilizce yaygın ret ifadeleridir.
    /// </summary>
    internal static bool IsRefusal(string answer) =>
        answer.Contains(ExtractiveChatCompletionService.NoAnswer, StringComparison.Ordinal) || Refusal().IsMatch(TextFolding.Fold(answer));

    [GeneratedRegex(@"\[(\d+(?:\s*,\s*\d+)*)\]")]
    private static partial Regex Citation();

    [GeneratedRegex(@"(yanit|bilgi)\s+(bulunmuyor|bulunmamaktadir|yer almiyor|yok\b)|yanit bulamadim|dogrudan bir bilgi|" +
                    @"\b(no|not)\s+(information|mentioned|answer)|do(es)? not (contain|mention|provide)")]
    private static partial Regex Refusal();

    [GeneratedRegex(@"(?<=[.!?])\s+|\n+")]
    private static partial Regex SentenceSplit();
}

/// <summary>Ajan 3 — Doğrulayıcı: sözcüksel dayanak + (isteğe bağlı) LLM yargıcı.</summary>
public sealed partial class VerifierAgent(Kernel kernel, GroundingScorer scorer, AgentOptions options)
{
    private const string JudgeInstructions =
        """
        Bir yanıtı denetliyorsun. YANIT'taki her iddia KAYNAKLAR tarafından destekleniyor mu?
        Yalnızca şu JSON'u döndür: {"supported": true|false, "reason": "<kısa gerekçe>"}
        """;

    public async Task<Verdict> VerifyAsync(string question, string answer, IReadOnlyList<SearchHit> sources, CancellationToken ct)
    {
        var lexical = scorer.Score(answer, sources);
        if (!options.VerifyWithLlm || lexical.Label == VerdictLabel.NoAnswer) return lexical;

        var agent = new ChatCompletionAgent
        {
            Name = "Dogrulayici",
            Instructions = JudgeInstructions,
            Kernel = kernel,
            Arguments = new KernelArguments(RetrieverAgent.Settings(0, 128)),
        };
        var prompt = PromptBuilder.BuildUserMessage(question, sources) + $"\nYANIT: {answer}";
        string raw = string.Empty;
        await foreach (var item in agent.InvokeAsync([new ChatMessageContent(AuthorRole.User, prompt)], cancellationToken: ct))
            raw += item.Message.Content;

        var judged = ParseJudge(raw);
        if (judged is null) return lexical with { Method = "lexical+llm", Note = "LLM yargıcı çözümlenemedi" };
        var (ok, reason) = judged.Value;
        var label = (lexical.Label, ok) switch
        {
            (VerdictLabel.Grounded, false) => VerdictLabel.Partial,
            (VerdictLabel.Partial, false) => VerdictLabel.Ungrounded,
            _ => lexical.Label,
        };
        return lexical with { Label = label, Method = "lexical+llm", Note = reason };
    }

    internal static (bool Supported, string Reason)? ParseJudge(string raw)
    {
        var m = JsonObject().Match(raw);
        if (!m.Success) return null;
        try
        {
            using var doc = JsonDocument.Parse(m.Value);
            var supported = doc.RootElement.GetProperty("supported").GetBoolean();
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return (supported, reason);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\{[^{}]*\}", RegexOptions.Singleline)]
    private static partial Regex JsonObject();
}
