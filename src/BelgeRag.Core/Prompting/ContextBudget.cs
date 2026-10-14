using System.Text;
using BelgeRag.Core.Chat;
using BelgeRag.Core.Search;
using BelgeRag.Core.Text;

namespace BelgeRag.Core.Prompting;

public sealed record TokenBudgetOptions
{
    /// <summary>Modelin context penceresi (Ollama'da num_ctx ile aynı tutulmalı).</summary>
    public int ContextWindowTokens { get; init; } = 4096;

    /// <summary>Yanıt üretimi için ayrılan token.</summary>
    public int ReservedForAnswer { get; init; } = 512;

    /// <summary>Tokenizer uyuşmazlığı (o200k ≠ model tokenizer'ı) için güvenlik payı, oran olarak.</summary>
    public double SafetyMargin { get; init; } = 0.10;

    /// <summary>Kalan bütçenin en fazla bu kadarı sohbet geçmişine gider.</summary>
    public double MaxHistoryShare { get; init; } = 0.25;

    /// <summary>Kesilerek sığdırılacak bir chunk'ın en az bu kadar token'ı kalmalı; daha azsa atlanır.</summary>
    public int MinChunkTokens { get; init; } = 48;
}

public sealed record BudgetedContext(
    IReadOnlyList<SearchHit> Sources,
    IReadOnlyList<ChatTurn> History,
    int PromptTokens,
    int AvailableTokens,
    int DroppedChunks,
    int TruncatedChunks,
    int DroppedHistoryTurns);

/// <summary>
/// Context window yönetimi: sabit kısımlar (sistem talimatı + soru) düşüldükten sonra kalan
/// bütçe önce geçmişe (en yeniden eskiye, payı sınırlı), sonra kaynaklara (rerank sırasıyla)
/// dağıtılır. Sığmayan kaynak, anlamlı bir kısmı sığıyorsa kesilir, yoksa atlanır.
/// </summary>
public sealed class ContextBudgeter(ITokenCounter tokens, TokenBudgetOptions options)
{
    public TokenBudgetOptions Options => options;

    public BudgetedContext Fit(string systemPrompt, string question, IReadOnlyList<SearchHit> ranked, IReadOnlyList<ChatTurn> history)
    {
        var window = (int)(options.ContextWindowTokens * (1 - options.SafetyMargin));
        var fixedTokens = tokens.Count(systemPrompt) + tokens.Count(question) + PromptBuilder.FrameOverheadTokens;
        var available = window - options.ReservedForAnswer - fixedTokens;
        if (available <= 0)
            throw new InvalidOperationException(
                $"Soru ve sistem talimatı context penceresine sığmıyor ({fixedTokens} token, pencere {options.ContextWindowTokens}).");
        var remaining = available;

        // 1) Geçmiş: en yeni turdan geriye, payı aşmadan.
        var historyBudget = (int)(available * options.MaxHistoryShare);
        var keptHistory = new List<ChatTurn>();
        foreach (var turn in history.Reverse())
        {
            var cost = tokens.Count(turn.Question) + tokens.Count(turn.Answer) + 8;
            if (cost > historyBudget) break;
            keptHistory.Insert(0, turn);
            historyBudget -= cost;
            remaining -= cost;
        }

        // 2) Kaynaklar: rerank sırasıyla.
        var kept = new List<SearchHit>();
        int dropped = 0, truncated = 0;
        foreach (var hit in ranked)
        {
            var header = PromptBuilder.SourceHeader(kept.Count + 1, hit.DocumentTitle);
            var cost = tokens.Count(header) + tokens.Count(hit.Text) + 2;
            if (cost <= remaining)
            {
                kept.Add(hit);
                remaining -= cost;
                continue;
            }
            var room = remaining - tokens.Count(header) - 2;
            if (room >= options.MinChunkTokens)
            {
                kept.Add(hit with { Text = tokens.Truncate(hit.Text, room) + " …" });
                remaining -= tokens.Count(header) + room + 4;
                truncated++;
            }
            else dropped++;
        }

        return new BudgetedContext(kept, keptHistory, fixedTokens + (available - remaining), available, dropped,
            truncated, history.Count - keptHistory.Count);
    }
}

public static class PromptBuilder
{
    /// <summary>"Kaynaklar:", "Soru:" gibi çerçeve metninin yaklaşık token maliyeti.</summary>
    internal const int FrameOverheadTokens = 24;

    public const string AnswerInstructions =
        """
        Sen bir belge asistanısın. Yalnızca verilen KAYNAKLAR'daki bilgiyle, soru hangi dildeyse o dilde yanıt ver.
        Her iddianın sonuna dayandığı kaynağın numarasını [1], [2] biçiminde ekle.
        Kaynaklarda yanıt yoksa bunu açıkça söyle ve tahmin yürütme. Kısa ve net yaz.
        """;

    public static string SourceHeader(int number, string title) => $"[{number}] ({title})";

    public static string BuildUserMessage(string question, IReadOnlyList<SearchHit> sources)
    {
        var sb = new StringBuilder();
        sb.AppendLine("KAYNAKLAR:");
        for (var i = 0; i < sources.Count; i++)
        {
            sb.Append(SourceHeader(i + 1, sources[i].DocumentTitle)).Append(' ').AppendLine(sources[i].Text.ReplaceLineEndings(" "));
            sb.AppendLine();
        }
        sb.Append("SORU: ").AppendLine(question);
        return sb.ToString();
    }

    /// <summary><see cref="BuildUserMessage"/> çıktısını kaynak listesine ve soruya geri ayrıştırır.</summary>
    public static (IReadOnlyList<(int Number, string Text)> Sources, string Question) Parse(string userMessage)
    {
        var sources = new List<(int, string)>();
        var question = userMessage;
        var qIdx = userMessage.LastIndexOf("SORU:", StringComparison.Ordinal);
        if (qIdx >= 0) question = userMessage[(qIdx + 5)..].Trim();
        var body = qIdx >= 0 ? userMessage[..qIdx] : userMessage;
        foreach (var line in body.Split('\n'))
        {
            var l = line.Trim();
            if (l.Length < 4 || l[0] != '[') continue;
            var close = l.IndexOf(']');
            if (close < 2 || !int.TryParse(l.AsSpan(1, close - 1), out var n)) continue;
            var text = l[(close + 1)..].Trim();
            if (text.StartsWith('('))
            {
                var tEnd = text.IndexOf(')');
                if (tEnd > 0) text = text[(tEnd + 1)..].Trim();
            }
            sources.Add((n, text));
        }
        return (sources, question);
    }
}
