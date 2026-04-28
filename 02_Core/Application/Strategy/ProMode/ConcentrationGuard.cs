using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.Strategy.ProMode;

/// <summary>
/// Position concentration enforcer. Prevents any single strategy/symbol/pair-leg from
/// consuming more than <see cref="MaxPerPositionWeight"/> of the total portfolio
/// capital, regardless of backtest performance or Kelly sizing.
///
/// Why it matters: the 2026-04-24 backtest showed TRX/XLM contributing 94% of
/// Aggressive-mode PnL — meaning one chain event / exchange delisting / hard fork
/// could erase most of the portfolio. This guard forces a HARD CEILING so no
/// single leg can become a single point of failure.
///
/// Defaults:
///   - 25% max per individual position (symbol or pair)
///   - 60% max per strategy family (pairs aggregate / funding aggregate)
///   - 40% max per underlying symbol across strategies (e.g. SOL appearing
///     in both SOL/AVAX pair AND a funding leg counts toward the SOL cap)
/// </summary>
public sealed class ConcentrationGuard
{
    private readonly ILogger<ConcentrationGuard> _logger;

    public decimal MaxPerPositionWeight { get; init; } = 0.25m;
    public decimal MaxPerStrategyFamilyWeight { get; init; } = 0.60m;
    public decimal MaxPerSymbolWeight { get; init; } = 0.40m;

    public ConcentrationGuard(ILogger<ConcentrationGuard> logger) { _logger = logger; }

    /// <summary>
    /// Takes a proposed allocation and clamps any over-concentrated weights to the
    /// per-position cap, redistributing the excess proportionally to the other
    /// positions (or returning as "excess cash" if no other position can absorb).
    /// </summary>
    public ConcentrationAdjustment Enforce(IReadOnlyList<PositionWeight> proposed)
    {
        if (proposed.Count == 0)
            return new ConcentrationAdjustment(proposed, 0m, Array.Empty<string>());

        var adjusted = proposed.Select(p => new PositionWeight(p.Name, p.SymbolKey, p.Family, p.Weight)).ToList();
        var violations = new List<string>();
        decimal excessToCash = 0m;

        // Pass 1 — per-position cap.
        for (int i = 0; i < adjusted.Count; i++)
        {
            if (adjusted[i].Weight > MaxPerPositionWeight)
            {
                var over = adjusted[i].Weight - MaxPerPositionWeight;
                violations.Add($"{adjusted[i].Name} capped: {adjusted[i].Weight:P0} → {MaxPerPositionWeight:P0}");
                adjusted[i] = adjusted[i] with { Weight = MaxPerPositionWeight };
                excessToCash += over;
            }
        }

        // Pass 2 — per-family cap (sum of all positions in family).
        var familyTotals = adjusted.GroupBy(p => p.Family)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Weight));
        foreach (var fam in familyTotals.Where(kv => kv.Value > MaxPerStrategyFamilyWeight))
        {
            var scale = MaxPerStrategyFamilyWeight / fam.Value;
            var shaved = 0m;
            for (int i = 0; i < adjusted.Count; i++)
            {
                if (!string.Equals(adjusted[i].Family, fam.Key, StringComparison.OrdinalIgnoreCase)) continue;
                var newW = adjusted[i].Weight * scale;
                shaved += adjusted[i].Weight - newW;
                adjusted[i] = adjusted[i] with { Weight = newW };
            }
            violations.Add($"Family '{fam.Key}' capped {fam.Value:P0} → {MaxPerStrategyFamilyWeight:P0} (shaved {shaved:P0})");
            excessToCash += shaved;
        }

        // Pass 3 — per-symbol cap.
        var symbolTotals = adjusted.GroupBy(p => p.SymbolKey)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Weight));
        foreach (var sym in symbolTotals.Where(kv => kv.Value > MaxPerSymbolWeight))
        {
            var scale = MaxPerSymbolWeight / sym.Value;
            var shaved = 0m;
            for (int i = 0; i < adjusted.Count; i++)
            {
                if (!string.Equals(adjusted[i].SymbolKey, sym.Key, StringComparison.OrdinalIgnoreCase)) continue;
                var newW = adjusted[i].Weight * scale;
                shaved += adjusted[i].Weight - newW;
                adjusted[i] = adjusted[i] with { Weight = newW };
            }
            violations.Add($"Symbol '{sym.Key}' capped {sym.Value:P0} → {MaxPerSymbolWeight:P0} (shaved {shaved:P0})");
            excessToCash += shaved;
        }

        if (violations.Count > 0)
            _logger.LogWarning("Concentration guard enforced {N} violations → {Excess:P0} shaved to cash",
                violations.Count, excessToCash);

        return new ConcentrationAdjustment(adjusted, excessToCash, violations);
    }
}

public sealed record PositionWeight(string Name, string SymbolKey, string Family, decimal Weight);

public sealed record ConcentrationAdjustment(
    IReadOnlyList<PositionWeight> Adjusted,
    decimal ExcessToCash,
    IReadOnlyList<string> Violations);
