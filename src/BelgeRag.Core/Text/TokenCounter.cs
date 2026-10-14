using Microsoft.ML.Tokenizers;

namespace BelgeRag.Core.Text;

public interface ITokenCounter
{
    int Count(string text);

    /// <summary>Metni en fazla <paramref name="maxTokens"/> token olacak şekilde baştan keser.</summary>
    string Truncate(string text, int maxTokens);
}

/// <summary>
/// o200k_base BPE sözlüğüyle token sayar. Yerel modellerin (ör. Gemma) kendi tokenizer'ı
/// farklıdır; bu sayım bütçe için yaklaşık bir üst sınır olarak kullanılır, bu yüzden
/// bütçede ayrıca güvenlik payı bırakılır (bkz. <see cref="Prompting.TokenBudgetOptions"/>).
/// Tokenizer iş parçacığı güvenli ve yüklemesi pahalı olduğundan Singleton yaşar.
/// </summary>
public sealed class TiktokenTokenCounter : ITokenCounter
{
    private readonly Tokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public int Count(string text) => string.IsNullOrEmpty(text) ? 0 : _tokenizer.CountTokens(text);

    public string Truncate(string text, int maxTokens)
    {
        if (maxTokens <= 0) return string.Empty;
        if (Count(text) <= maxTokens) return text;
        var index = _tokenizer.GetIndexByTokenCount(text, maxTokens, out _, out _);
        return text[..index];
    }
}
