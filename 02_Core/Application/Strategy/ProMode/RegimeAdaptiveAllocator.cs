using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy.ProMode;

/// <summary>
/// Level 2.8 — Regime-Adaptive Portfolio Allocator.
///
/// Given the current market regime, returns a capital-weight map across the three
/// strategy families + CASH. Cash is a POSITION, not idle capital — "sitting out" is
/// a deliberate stance in hostile regimes.
///
/// Weight philosophy (based on academic + empirical evidence):
///   • Trending:  pairs 30 · funding 15 · trend 45 · cash 10
///     → momentum carries; pairs cointegration can break during sharp trends, so reduced
///   • Ranging:   pairs 50 · funding 40 · trend 0 · cash 10
///     → this is our bread-and-butter environment; pairs mean-revert cleanly
///   • Volatile:  pairs 20 · funding 10 · trend 0 · cash 70
///     → HIGH CASH — cointegration breaks + fees crush marginal edges in crises
///   • Unknown:   pairs 30 · funding 20 · trend 0 · cash 50
///     → cautious default until regime stabilises
///
/// The trend bucket is currently UNFUNDED (no validated trend strategy) — its weight
/// falls back to cash when the allocator requests it. Future Phase adds trend-follow.
/// </summary>
public static class RegimeAdaptiveAllocator
{
    public static PortfolioAllocation Compute(MarketRegime regime, bool hasValidatedTrendStrategy = false)
    {
        // 2026-04-24 REFINED (Opsiyon C): behaviour simulation revealed generic academic
        // weights dragged return by 42-48% because our VALIDATED strategies (TRX/XLM,
        // SOL/AVAX, SOL/LINK pairs + funding fade) empirically work in BOTH Trending
        // AND Ranging. Only Volatile regime poses a structural threat (cointegration
        // breaks + fee amplification). So we apply defensive cash ONLY in Volatile.
        // Trending + Ranging = full deploy (baseline 6-pos weights preserved).
        // Unknown = cautious default.
        var (pairs, funding, trend, cash) = regime switch
        {
            MarketRegime.Trending => (0.50m, 0.40m, 0.00m, 0.10m),  // full deploy — strategies validated here
            MarketRegime.Ranging => (0.50m, 0.40m, 0.00m, 0.10m),   // sweet spot — full deploy
            MarketRegime.Volatile => (0.20m, 0.10m, 0.00m, 0.70m),  // INSURANCE — crisis protection
            _ => (0.30m, 0.20m, 0.00m, 0.50m)                       // Unknown — cautious
        };

        // If trend strategy isn't validated yet, redirect its allocation to cash (safer
        // than leaving capital idle on an untested bucket).
        if (!hasValidatedTrendStrategy && trend > 0m)
        {
            cash += trend;
            trend = 0m;
        }

        return new PortfolioAllocation(
            Regime: regime,
            PairsWeight: pairs,
            FundingWeight: funding,
            TrendFollowWeight: trend,
            CashWeight: cash,
            Reason: ExplainAllocation(regime, pairs, funding, trend, cash));
    }

    private static string ExplainAllocation(MarketRegime regime, decimal pairs, decimal funding, decimal trend, decimal cash)
    {
        return regime switch
        {
            MarketRegime.Trending => $"Trending regime — strategies validated here; FULL DEPLOY ({pairs:P0} pairs · {funding:P0} funding · {cash:P0} cash).",
            MarketRegime.Ranging => $"Ranging regime — sweet spot for mean-reversion strategies; FULL DEPLOY ({pairs:P0} pairs · {funding:P0} funding · {cash:P0} cash).",
            MarketRegime.Volatile => $"Volatile regime — INSURANCE MODE. {cash:P0} cash, pairs cut to {pairs:P0} (cointegration fragile), funding to {funding:P0} (fees amplify in vol spikes). This is the only regime where structural break risk justifies defensive posture.",
            _ => $"Unknown regime — cautious default. {cash:P0} cash until regime stabilises."
        };
    }
}

/// <summary>
/// Full allocation pipeline: regime → bucket weights → per-position weights scaled by
/// bucket share → concentration cap enforced → excess to cash. The ONE place to call
/// for final position-level weights in Pro Mode.
///
/// Example:
///   var pipeline = new AllocationPipeline(concentrationGuard);
///   var final = pipeline.Compute(currentRegime, candidatePositions);
///   // final.CashWeight now includes regime-cash + concentration-excess-to-cash.
/// </summary>
public sealed class AllocationPipeline
{
    private readonly ConcentrationGuard _concentrationGuard;
    private readonly ILogger<AllocationPipeline>? _logger;

    public AllocationPipeline(ConcentrationGuard concentrationGuard, ILogger<AllocationPipeline>? logger = null)
    {
        _concentrationGuard = concentrationGuard;
        _logger = logger;
    }

    public FinalAllocation Compute(MarketRegime regime, IReadOnlyList<CandidatePosition> candidates)
    {
        var bucketWeights = RegimeAdaptiveAllocator.Compute(regime);

        // Scale each candidate's intended weight by its bucket's regime-allocation.
        // Example: candidate TRX/XLM with intended 30% in Pairs family × regime Pairs weight 50%
        // = 15% pre-concentration-cap. Then concentration guard re-caps.
        var proposed = candidates.Select(c =>
        {
            decimal bucketShare = c.Family switch
            {
                "Pairs" => bucketWeights.PairsWeight,
                "Funding" => bucketWeights.FundingWeight,
                "Trend" => bucketWeights.TrendFollowWeight,
                _ => 0m
            };
            // Candidates within a family divide that family's share proportional to their
            // intended weight (which should sum to 1.0 within the family for clean math).
            return new PositionWeight(c.Name, c.SymbolKey, c.Family, c.IntendedWeightWithinFamily * bucketShare);
        }).ToList();

        var adjusted = _concentrationGuard.Enforce(proposed);
        decimal finalCash = bucketWeights.CashWeight + adjusted.ExcessToCash;

        _logger?.LogInformation(
            "AllocationPipeline: regime {Regime} → {N} positions, cash {Cash:P0} (regime {RegCash:P0} + excess {Excess:P0}), {V} concentration violations",
            regime, adjusted.Adjusted.Count, finalCash, bucketWeights.CashWeight, adjusted.ExcessToCash, adjusted.Violations.Count);

        return new FinalAllocation(
            Regime: regime,
            Positions: adjusted.Adjusted,
            CashWeight: finalCash,
            ConcentrationViolations: adjusted.Violations,
            Reason: bucketWeights.Reason);
    }
}

public sealed record CandidatePosition(
    string Name,
    string SymbolKey,
    string Family,                        // "Pairs" / "Funding" / "Trend"
    decimal IntendedWeightWithinFamily);  // 0-1, sum within family should ≈ 1

public sealed record FinalAllocation(
    MarketRegime Regime,
    IReadOnlyList<PositionWeight> Positions,
    decimal CashWeight,
    IReadOnlyList<string> ConcentrationViolations,
    string Reason);

/// <summary>
/// Portfolio allocation weights for a given regime. Weights sum to 1.0 (invariant enforced
/// at construction — any "ghost" allocation from untested strategies folds into cash).
/// </summary>
public sealed record PortfolioAllocation(
    MarketRegime Regime,
    decimal PairsWeight,
    decimal FundingWeight,
    decimal TrendFollowWeight,
    decimal CashWeight,
    string Reason)
{
    public decimal TotalDeployed => PairsWeight + FundingWeight + TrendFollowWeight;
    public bool IsDefensive => CashWeight >= 0.50m;
}
