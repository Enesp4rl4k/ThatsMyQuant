namespace ThatsMyQuant.Contracts.Options;

/// <summary>
/// Strategy promotion gate options (section: Strategy:Promotion).
/// Controls cross-bucket fallback behaviour + strict-isolation mode for
/// multi-symbol portfolios where one symbol's performance must NOT influence
/// another symbol's promotion decisions.
/// </summary>
public sealed class StrategyPromotionOptions
{
    /// <summary>
    /// When true, a bucket match is only considered if the Symbol matches
    /// exactly. Prevents cross-symbol bleed in the fallback cascade: without
    /// this, Symbol B's promotion decision can be biased by Symbol A's
    /// historical performance on the same (Strategy, Regime) pair.
    ///
    /// Default true — safe for paper/live multi-symbol portfolios.
    /// Set false to restore legacy warm-start behaviour (useful in cold-start
    /// research where any prior evidence is better than none).
    /// </summary>
    public bool StrictSymbolIsolation { get; set; } = true;

    /// <summary>
    /// Penalty multiplier applied when a fallback (non-exact) match is used
    /// while strict isolation is OFF. Discounts less-specific evidence so
    /// cross-symbol warm-start carries appropriately less weight.
    /// </summary>
    public decimal CrossSymbolFallbackPenalty { get; set; } = 0.80m;
}
