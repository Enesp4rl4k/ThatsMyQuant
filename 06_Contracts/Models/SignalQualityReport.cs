namespace ThatsMyQuant.Contracts.Models;

/// <summary>
/// Aggregate alpha-predictiveness report computed from a collection of
/// <see cref="SignalAuditRecord"/>s. Captures information coefficient,
/// signal-alone cumulative PnL, and conditional performance by strategy
/// family, regime, and confidence bucket.
/// </summary>
public sealed record SignalQualityReport
{
    public required string Symbol { get; init; }
    public required string Timeframe { get; init; }
    public required DateTime PeriodStart { get; init; }
    public required DateTime PeriodEnd { get; init; }

    /// <summary>Number of candles walked (post-warmup, pre-horizon-cutoff).</summary>
    public required int CandlesEvaluated { get; init; }

    /// <summary>Count of candles where ShouldTrade==true (candidate signals).</summary>
    public required int Candidates { get; init; }

    public required SignalReturnStats Overall { get; init; }

    /// <summary>Spearman rank IC between confidence and forward returns.</summary>
    public required InformationCoefficient IC { get; init; }

    /// <summary>Breakdown by StrategyTag (e.g. TrendConfluence, RangeReversion).</summary>
    public required IReadOnlyDictionary<string, SignalReturnStats> ByStrategy { get; init; }

    /// <summary>Breakdown by detected regime (Trending/Ranging/Volatile).</summary>
    public required IReadOnlyDictionary<string, SignalReturnStats> ByRegime { get; init; }

    /// <summary>Breakdown by confidence bucket (60-69, 70-79, 80-89, 90+).</summary>
    public required IReadOnlyDictionary<string, SignalReturnStats> ByConfidenceBucket { get; init; }

    /// <summary>
    /// Signal-alone cumulative equity curve using fwd10 as the "hold to horizon"
    /// return — NO risk management, NO sizing, pure signal-direction PnL.
    /// Final value is the sum of all fwd10 returns across candidate signals.
    /// </summary>
    public required decimal SignalAloneCumulativeReturnPercent { get; init; }

    /// <summary>Same for fwd20 horizon.</summary>
    public required decimal SignalAloneCumulativeReturn20Percent { get; init; }

    /// <summary>
    /// Per-feature Spearman IC measured independently. Positive = feature supports
    /// actual future direction; negative = feature is anti-predictive and its
    /// contribution to confidence should be flipped or removed.
    /// </summary>
    public required IReadOnlyDictionary<string, FeatureIc> ByFeatureIc { get; init; }
}

public sealed record FeatureIc
{
    public required int N { get; init; }
    public required double IcFwd10 { get; init; }
    public required double IcFwd20 { get; init; }
    /// <summary>Mean forward10 when the feature's net score is >0 (supports decision).</summary>
    public required decimal MeanFwd10WhenPositive { get; init; }
    /// <summary>Mean forward10 when the feature's net score is <=0 (silent or against).</summary>
    public required decimal MeanFwd10WhenNonPositive { get; init; }
}

public sealed record SignalReturnStats
{
    public required int N { get; init; }
    public required decimal MeanFwd1 { get; init; }
    public required decimal MeanFwd5 { get; init; }
    public required decimal MeanFwd10 { get; init; }
    public required decimal MeanFwd20 { get; init; }
    public required decimal MeanFwd50 { get; init; }
    public required decimal MedianFwd10 { get; init; }
    public required decimal HitRateFwd10 { get; init; }  // fraction > 0
    public required decimal HitRateFwd20 { get; init; }
}

public sealed record InformationCoefficient
{
    public required double Fwd1 { get; init; }
    public required double Fwd5 { get; init; }
    public required double Fwd10 { get; init; }
    public required double Fwd20 { get; init; }
    public required double Fwd50 { get; init; }
}
