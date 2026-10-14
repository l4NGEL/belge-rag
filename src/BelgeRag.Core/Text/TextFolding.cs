using System.Text;

namespace BelgeRag.Core.Text;

/// <summary>
/// Sözcüksel karşılaştırmalar (rerank, hashing embedding, dayanak kontrolü) için metni
/// kültürden bağımsız tek bir biçime indirger. Türkçe "I/ı/İ/i" sorunu yüzünden
/// ToLowerInvariant tek başına yetmez: "İSTANBUL" → "i̇stanbul" (birleşik nokta) olur.
/// Bu yüzden önce Türkçe harfler ASCII karşılıklarına katlanır, sonra küçültülür.
/// </summary>
public static class TextFolding
{
    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                'ı' or 'I' or 'İ' or 'i' => 'i',
                'ş' or 'Ş' => 's',
                'ğ' or 'Ğ' => 'g',
                'ü' or 'Ü' => 'u',
                'ö' or 'Ö' => 'o',
                'ç' or 'Ç' => 'c',
                'â' or 'Â' => 'a',
                'î' or 'Î' => 'i',
                'û' or 'Û' => 'u',
                _ => char.ToLowerInvariant(ch),
            });
        }
        return sb.ToString();
    }

    /// <summary>Katlanmış metni harf/rakam dizilerine böler.</summary>
    public static IEnumerable<string> Tokens(string text)
    {
        var folded = Fold(text);
        var start = -1;
        for (var i = 0; i <= folded.Length; i++)
        {
            var isWord = i < folded.Length && char.IsLetterOrDigit(folded[i]);
            if (isWord && start < 0) start = i;
            else if (!isWord && start >= 0)
            {
                yield return folded[start..i];
                start = -1;
            }
        }
    }

    /// <summary>
    /// Anlam taşıyan terimler: 2 karakterden uzun ve durak listesinde olmayan tokenlar.
    /// Türkçe ekler için kaba bir kök yaklaşımı olarak ilk 6 karakter alınır
    /// ("modelin", "modeller" → "modeli"/"modell" değil, "model" ön eki üzerinden eşleşir).
    /// </summary>
    public static IEnumerable<string> ContentTerms(string text, int stemLength = 6)
    {
        foreach (var t in Tokens(text))
        {
            if (t.Length < 3 || StopWords.Contains(t)) continue;
            yield return t.Length > stemLength ? t[..stemLength] : t;
        }
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // Türkçe (katlanmış biçimde)
        "ve", "ile", "icin", "bir", "bu", "su", "da", "de", "mi", "mu", "ne", "nasil", "neden",
        "hangi", "kac", "olan", "olarak", "gibi", "daha", "cok", "her", "ise", "ama", "veya",
        "kadar", "sonra", "once", "uzerinde", "icinde", "nedir", "midir", "var", "yok", "hem",
        "tum", "ayni", "diye", "yani", "ki", "en", "olur", "eder", "edilir", "yapilir",
        // İngilizce
        "the", "and", "for", "with", "that", "this", "from", "are", "was", "were", "what",
        "which", "how", "why", "into", "its", "has", "have", "not", "but", "you", "your",
    };
}
