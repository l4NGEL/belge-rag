using System.Text.RegularExpressions;

namespace BelgeRag.Core.Text;

public sealed record ChunkingOptions
{
    public int MaxTokens { get; init; } = 220;
    public int OverlapTokens { get; init; } = 40;
}

public sealed record TextChunk(int Ordinal, string Text, int TokenCount);

/// <summary>
/// Token sınırlı, örtüşmeli parçalayıcı. Önce Markdown başlık/paragraf sınırlarına,
/// sığmazsa cümlelere, o da sığmazsa sözcüklere iner. Her parça, kendisinden önceki
/// parçanın son ~OverlapTokens'lık cümleleriyle başlar; böylece sınırda bölünen bilgi kaybolmaz.
/// Her parçanın başına bulunduğu bölümün başlığı eklenir (başlık bağlamı aramada çok işe yarar).
/// </summary>
public sealed partial class TextChunker(ITokenCounter tokens)
{
    public IReadOnlyList<TextChunk> Chunk(string text, ChunkingOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxTokens, 16);
        ArgumentOutOfRangeException.ThrowIfNegative(options.OverlapTokens);
        if (options.OverlapTokens >= options.MaxTokens / 2)
            throw new ArgumentException("Örtüşme, parça boyunun yarısından küçük olmalı.", nameof(options));

        var units = SplitIntoUnits(text, options.MaxTokens);
        var result = new List<TextChunk>();
        var current = new List<Unit>();
        var currentTokens = 0;
        var fresh = 0; // son Flush'tan beri eklenen (henüz yazılmamış) birim sayısı

        void Flush()
        {
            if (fresh == 0) return;
            var heading = current[0].Heading;
            var body = string.Join(" ", current.Select(u => u.Text));
            var chunkText = heading is null ? body : $"{heading}\n{body}";
            result.Add(new TextChunk(result.Count, chunkText, tokens.Count(chunkText)));

            // Örtüşme: sondan geriye doğru OverlapTokens'ı aşmayan cümleleri yeni parçaya taşı.
            var carry = new List<Unit>();
            var carryTokens = 0;
            for (var i = current.Count - 1; i >= 0; i--)
            {
                if (carryTokens + current[i].Tokens > options.OverlapTokens) break;
                carry.Insert(0, current[i]);
                carryTokens += current[i].Tokens;
            }
            current = carry;
            currentTokens = carryTokens;
            fresh = 0;
        }

        foreach (var unit in units)
        {
            if (current.Count > 0 && unit.Heading != current[^1].Heading)
            {
                // Yeni bölüm: önceki bölümü yaz, örtüşmeyi bölüm sınırının ötesine taşıma.
                Flush();
                current.Clear();
                currentTokens = 0;
            }
            if (currentTokens + unit.Tokens > options.MaxTokens) Flush();
            // Örtüşme + yeni birim yine sığmıyorsa örtüşmeyi bırak.
            if (currentTokens + unit.Tokens > options.MaxTokens) { current.Clear(); currentTokens = 0; }
            current.Add(unit);
            currentTokens += unit.Tokens;
            fresh++;
        }
        Flush();
        return result;
    }

    private readonly record struct Unit(string Text, int Tokens, string? Heading);

    private List<Unit> SplitIntoUnits(string text, int maxTokens)
    {
        var units = new List<Unit>();
        string? heading = null;
        var normalized = text.Replace("\r\n", "\n");
        foreach (var rawBlock in BlockSplit().Split(normalized))
        {
            var block = rawBlock.Trim();
            if (block.Length == 0) continue;

            var lines = block.Split('\n');
            if (lines[0].StartsWith('#'))
            {
                heading = lines[0].TrimStart('#').Trim();
                block = string.Join("\n", lines.Skip(1)).Trim();
                if (block.Length == 0) continue;
            }

            foreach (var sentence in SentenceSplit().Split(block))
            {
                var s = WhitespaceRun().Replace(sentence, " ").Trim();
                if (s.Length == 0) continue;
                var count = tokens.Count(s);
                if (count <= maxTokens)
                {
                    units.Add(new Unit(s, count, heading));
                    continue;
                }
                // Tek cümle sınırdan büyükse (ör. uzun tablo satırı) sözcük pencerelerine böl.
                var words = s.Split(' ');
                var window = new List<string>();
                foreach (var w in words)
                {
                    window.Add(w);
                    if (tokens.Count(string.Join(' ', window)) > maxTokens / 2)
                    {
                        var piece = string.Join(' ', window);
                        units.Add(new Unit(piece, tokens.Count(piece), heading));
                        window.Clear();
                    }
                }
                if (window.Count > 0)
                {
                    var piece = string.Join(' ', window);
                    units.Add(new Unit(piece, tokens.Count(piece), heading));
                }
            }
        }
        return units;
    }

    [GeneratedRegex(@"\n\s*\n|\n(?=#)")]
    private static partial Regex BlockSplit();

    // Cümle sonu: . ! ? ardından boşluk ve büyük harf/rakam/madde işareti; "v1.2" gibi ondalıkları bölmez.
    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-ZÇĞİÖŞÜ0-9\-\*\(""])|\n(?=\s*[-*|] )")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
