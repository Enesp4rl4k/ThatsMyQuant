// Feature: watchlist-exchange-sync, Property 3: Union-with-dedup
// Feature: watchlist-exchange-sync, Property 4: No-removal
// Validates: Requirements 4.1, 4.2, 4.3, 4.4, 4.5, 9.3, 9.4, 9.5

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using ThatsMyQuant.Contracts.Helpers;

namespace ThatsMyQuant.Tests.Watchlist;

public class WatchlistMergeTests
{
    // ── character sets ────────────────────────────────────────────────────────

    private static readonly char[] UpperAlpha    = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();
    private static readonly char[] UpperAlphaNum = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray();

    // ── generators ────────────────────────────────────────────────────────────

    private static Gen<char> PickChar(char[] pool) => Gen.Elements<char>(pool);

    private static Gen<string> StringOfLen(int len, char[] pool) =>
        Gen.ArrayOf<char>(PickChar(pool), len).Select(cs => new string(cs));

    /// <summary>
    /// Generates a single canonical symbol matching ^[A-Z][A-Z0-9]{1,19}$
    /// </summary>
    private static Gen<string> CanonicalSymbolGen() =>
        from firstChar in PickChar(UpperAlpha)
        from tailLen   in Gen.Choose(1, 9)
        from tail      in StringOfLen(tailLen, UpperAlphaNum)
        select firstChar + tail;

    /// <summary>
    /// Generates a list of 0–10 canonical symbols (may contain duplicates).
    /// </summary>
    private static Gen<List<string>> SymbolListGen() =>
        from count   in Gen.Choose(0, 10)
        from symbols in Gen.ArrayOf<string>(CanonicalSymbolGen(), count)
        select symbols.ToList();

    // ── pure merge helper (mirrors MainViewModel.WatchlistSync logic) ─────────

    /// <summary>
    /// Replicates the merge/dedup logic from MainViewModel.SyncWatchlist:
    ///   result = existing + (okx ∪ binance).Where(IsValid).Distinct(OrdinalIgnoreCase).Where(not in existing)
    /// </summary>
    private static List<string> MergeSymbols(
        IEnumerable<string> existing,
        IEnumerable<string> okxSymbols,
        IEnumerable<string> binanceSymbols)
    {
        var existingList = existing.ToList();
        var existingSet  = existingList.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newSymbols = okxSymbols
            .Concat(binanceSymbols)
            .Where(SymbolNormalizer.IsValid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(s => !existingSet.Contains(s))
            .ToList();

        return existingList.Concat(newSymbols).ToList();
    }

    // ── Property 3: Union-with-dedup ──────────────────────────────────────────
    // For any existing, okxSymbols, binanceSymbols lists of canonical symbols,
    // every unique valid symbol from all three inputs appears exactly once in result.
    // Validates: Requirements 4.2, 4.3, 4.5, 9.3, 9.5
    [Property(MaxTest = 100)]
    public Property P3_UnionWithDedup()
    {
        // Feature: watchlist-exchange-sync, Property 3
        var gen =
            from existing  in SymbolListGen()
            from okx       in SymbolListGen()
            from binance   in SymbolListGen()
            select (existing, okx, binance);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (existing, okx, binance) = tuple;

            var result = MergeSymbols(existing, okx, binance);

            // Build the full set of unique valid symbols from all inputs
            var allValid = existing
                .Concat(okx)
                .Concat(binance)
                .Where(SymbolNormalizer.IsValid)
                .Select(s => s.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var resultUpper = result.Select(s => s.ToUpperInvariant()).ToList();

            // Every unique valid symbol from all inputs must appear in result
            bool allPresent = allValid.All(sym => resultUpper.Contains(sym));

            // Each symbol appears exactly once (case-insensitive)
            bool noDuplicates = resultUpper
                .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .All(g => g.Count() == 1);

            return allPresent && noDuplicates;
        });
    }

    // ── Property 4: No-removal ────────────────────────────────────────────────
    // For any existing, okxSymbols, binanceSymbols:
    //   result.Count >= existing.Count  AND  every symbol in existing is still in result.
    // Validates: Requirements 4.1, 4.4, 9.4
    [Property(MaxTest = 100)]
    public Property P4_NoRemoval()
    {
        // Feature: watchlist-exchange-sync, Property 4
        var gen =
            from existing in SymbolListGen()
            from okx      in SymbolListGen()
            from binance  in SymbolListGen()
            select (existing, okx, binance);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (existing, okx, binance) = tuple;

            var result = MergeSymbols(existing, okx, binance);

            // Count must not decrease
            bool countNonDecreasing = result.Count >= existing.Count;

            // Every pre-existing symbol must still be present
            var resultSet = result.ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool allExistingPreserved = existing.All(sym => resultSet.Contains(sym));

            return countNonDecreasing && allExistingPreserved;
        });
    }
}
