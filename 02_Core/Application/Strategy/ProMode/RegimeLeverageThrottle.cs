using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy.ProMode;

/// <summary>
/// Regime-aware leverage throttle. Given a base leverage (from the user's risk profile)
/// and the current market regime, returns the effective leverage to use for NEW entries.
///
/// Rationale: volatile regimes inflate drawdown at leveraged positions because stop-outs
/// happen more frequently AND fees scale linearly. In a sustained Trending or Ranging
/// regime with normal volatility, the base leverage is fine. In a Volatile regime
/// (GARCH vol expansion, ATR spike), we DEFENSIVELY halve leverage.
///
/// This is pure — no side effects, trivially testable. Wire into the runner's entry
/// sizing by multiplying base leverage by <see cref="Compute"/>.
/// </summary>
public static class RegimeLeverageThrottle
{
    /// <summary>
    /// Computes an effective leverage based on regime. Volatile → ×0.5, Volatile+Trending
    /// disagreement → ×0.7 (transitional), Trending/Ranging → ×1.0.
    /// </summary>
    public static decimal Compute(decimal baseLeverage, MarketRegime regime)
    {
        if (baseLeverage <= 1.0m) return baseLeverage;  // unleveraged — no throttle

        return regime switch
        {
            MarketRegime.Volatile => baseLeverage * 0.5m,   // DEFENSIVE: halve in high-vol
            MarketRegime.Trending => baseLeverage,           // normal — momentum in our favor
            MarketRegime.Ranging => baseLeverage,            // normal — mean-rev works best here
            MarketRegime.Unknown => baseLeverage * 0.75m,    // caution on insufficient data
            _ => baseLeverage
        };
    }

    /// <summary>
    /// Human-readable reason code for audit / UI display.
    /// </summary>
    public static string Explain(decimal baseLeverage, MarketRegime regime, decimal effective)
    {
        if (baseLeverage <= 1.0m) return $"Unleveraged ({baseLeverage:F1}x) — throttle inactive";
        var ratio = effective / baseLeverage;
        return regime switch
        {
            MarketRegime.Volatile => $"Volatile regime — leverage {baseLeverage:F1}x → {effective:F1}x (×{ratio:F2} defensive)",
            MarketRegime.Trending => $"Trending regime — full leverage {effective:F1}x",
            MarketRegime.Ranging => $"Ranging regime — full leverage {effective:F1}x",
            _ => $"Unknown regime — leverage {baseLeverage:F1}x → {effective:F1}x (×{ratio:F2} cautious)"
        };
    }
}
