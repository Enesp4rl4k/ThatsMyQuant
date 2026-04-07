namespace ThatsMyQuant.Contracts.Helpers;

public static class SymbolNormalizer
{
    private static readonly string[] QuoteAssets = ["USDT", "USDC", "BTC", "ETH", "EUR", "TRY"];

    /// <summary>
    /// Converts any exchange symbol to canonical form: removes hyphens, uppercases.
    /// e.g. "BTC-USDT" → "BTCUSDT", "btcusdt" → "BTCUSDT"
    /// </summary>
    public static string ToCanonical(string symbol)
        => symbol.Replace("-", string.Empty).ToUpperInvariant();

    /// <summary>
    /// Converts canonical symbol to OKX hyphenated format.
    /// e.g. "BTCUSDT" → "BTC-USDT"
    /// Returns the canonical symbol unchanged if no known quote asset matches.
    /// </summary>
    public static string ToOkx(string canonical)
    {
        foreach (var quote in QuoteAssets)
        {
            if (canonical.EndsWith(quote, StringComparison.Ordinal) && canonical.Length > quote.Length)
                return $"{canonical[..^quote.Length]}-{quote}";
        }
        return canonical;
    }

    /// <summary>
    /// Returns true if the symbol matches ^[A-Z][A-Z0-9]{1,19}$
    /// </summary>
    public static bool IsValid(string canonical)
        => !string.IsNullOrEmpty(canonical)
           && System.Text.RegularExpressions.Regex.IsMatch(canonical, @"^[A-Z][A-Z0-9]{1,19}$");
}
