// Feature: watchlist-exchange-sync, Property 1: OKX symbol round-trip
// Feature: watchlist-exchange-sync, Property 2: Normalization idempotence
// Feature: watchlist-exchange-sync, Property 5: CanonicalSymbol format invariant
// Validates: Requirements 2.3, 2.4, 2.5, 9.1, 9.2, 9.6

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using ThatsMyQuant.Contracts.Helpers;

namespace ThatsMyQuant.Tests.Watchlist;

public class SymbolNormalizerTests
{
    // ── character sets ────────────────────────────────────────────────────────

    private static readonly char[] UpperAlpha = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();
    private static readonly char[] UpperAlphaNum = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray();

    private static readonly string[] QuoteAssets = ["USDT", "USDC", "BTC", "ETH", "EUR", "TRY"];

    // ── helpers ───────────────────────────────────────────────────────────────

    private static Gen<char> PickChar(char[] pool) => Gen.Elements<char>(pool);

    private static Gen<string> StringOfLen(int len, char[] pool) =>
        Gen.ArrayOf<char>(PickChar(pool), len).Select(cs => new string(cs));

    // ── Property 1: OKX round-trip ────────────────────────────────────────────
    // For any valid OKX symbol {BASE}-{QUOTE}, converting to canonical and back
    // must produce a symbol equivalent to the original.
    // Validates: Requirements 2.4, 9.1
    [Property(MaxTest = 100)]
    public Property P1_OkxRoundTrip()
    {
        // Feature: watchlist-exchange-sync, Property 1
        var gen =
            from baseLen in Gen.Choose(2, 5)
            from baseStr in StringOfLen(baseLen, UpperAlpha)
            from quote in Gen.Elements<string>(QuoteAssets)
            select $"{baseStr}-{quote}";

        return Prop.ForAll(gen.ToArbitrary(), okxSymbol =>
        {
            var canonical = SymbolNormalizer.ToCanonical(okxSymbol);
            var backToOkx = SymbolNormalizer.ToOkx(canonical);
            var roundTripped = SymbolNormalizer.ToCanonical(backToOkx);
            return roundTripped == canonical;
        });
    }

    // ── Property 2: Idempotence ───────────────────────────────────────────────
    // Applying ToCanonical twice must produce the same result as once.
    // Validates: Requirements 2.3, 9.2
    [Property(MaxTest = 100)]
    public Property P2_ToCanonical_Idempotent(NonNull<string> input)
    {
        // Feature: watchlist-exchange-sync, Property 2
        var x = input.Get;
        var once = SymbolNormalizer.ToCanonical(x);
        var twice = SymbolNormalizer.ToCanonical(once);
        return (twice == once).ToProperty();
    }

    // ── Property 5: Format invariant ─────────────────────────────────────────
    // Strings matching ^[A-Z][A-Z0-9]{1,19}$ must pass IsValid;
    // strings that do not match must fail IsValid.
    // Validates: Requirements 2.5, 9.6
    [Property(MaxTest = 100)]
    public Property P5a_ValidFormat_IsValid()
    {
        // Feature: watchlist-exchange-sync, Property 5 (valid branch)
        // Generate strings matching ^[A-Z][A-Z0-9]{1,19}$
        var gen =
            from firstChar in PickChar(UpperAlpha)
            from tailLen in Gen.Choose(1, 19)
            from tail in StringOfLen(tailLen, UpperAlphaNum)
            select firstChar + tail;

        return Prop.ForAll(gen.ToArbitrary(), symbol =>
            SymbolNormalizer.IsValid(symbol));
    }

    [Property(MaxTest = 100)]
    public Property P5b_InvalidFormat_IsNotValid()
    {
        // Feature: watchlist-exchange-sync, Property 5 (invalid branch)
        // Generate strings that violate the pattern in one of several ways:
        //   - empty string
        //   - starts with a digit
        //   - contains a lowercase letter
        //   - contains a hyphen
        //   - too long (> 20 chars)
        //   - too short (1 char, needs at least 2)
        var startsWithDigit =
            from d in PickChar("0123456789".ToCharArray())
            from tailLen in Gen.Choose(1, 10)
            from tail in StringOfLen(tailLen, UpperAlphaNum)
            select d + tail;

        var containsLower =
            from firstChar in PickChar(UpperAlpha)
            from lower in PickChar("abcdefghijklmnopqrstuvwxyz".ToCharArray())
            from tailLen in Gen.Choose(0, 8)
            from tail in StringOfLen(tailLen, UpperAlphaNum)
            select firstChar + lower + tail;

        var containsHyphen =
            from firstChar in PickChar(UpperAlpha)
            from tailLen in Gen.Choose(1, 8)
            from tail in StringOfLen(tailLen, UpperAlphaNum)
            select firstChar + "-" + tail;

        var tooLong =
            from firstChar in PickChar(UpperAlpha)
            from tail in StringOfLen(20, UpperAlphaNum)   // 1 + 20 = 21 chars
            select firstChar + tail;

        var singleChar =
            from c in PickChar(UpperAlpha)
            select c.ToString();

        var emptyGen = Gen.Constant(string.Empty);

        var invalidGen = Gen.OneOf(startsWithDigit, containsLower, containsHyphen, tooLong, singleChar, emptyGen);

        return Prop.ForAll(invalidGen.ToArbitrary(), symbol =>
            !SymbolNormalizer.IsValid(symbol));
    }
}
