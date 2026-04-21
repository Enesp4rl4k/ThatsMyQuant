using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Contracts.Models;

/// <summary>
/// Single signal observation captured during a signal-quality audit.
/// No execution is performed — we just record what the decision chain believed
/// at this candle and the realized forward returns so alpha predictiveness can
/// be measured offline.
/// </summary>
public sealed record SignalAuditRecord
{
    public required DateTime Timestamp { get; init; }
    public required string Symbol { get; init; }

    /// <summary>Whether the (post-router, post-inference) decision recommended a trade.</summary>
    public required bool ShouldTrade { get; init; }

    public required TradeSide Side { get; init; }

    /// <summary>Final confidence after router + inference overlay (0-100).</summary>
    public required int Confidence { get; init; }

    public required string StrategyTag { get; init; }
    public required string RegimeTag { get; init; }

    /// <summary>Entry candle close — reference price for forward returns.</summary>
    public required decimal EntryPrice { get; init; }

    /// <summary>
    /// Signed forward return in percent at bar horizons 1/5/10/20/50.
    /// Signed so that "good for the signal direction" is always positive:
    ///   Buy:  (future - entry) / entry * 100
    ///   Sell: (entry - future) / entry * 100
    /// </summary>
    public decimal Fwd1 { get; init; }
    public decimal Fwd5 { get; init; }
    public decimal Fwd10 { get; init; }
    public decimal Fwd20 { get; init; }
    public decimal Fwd50 { get; init; }

    /// <summary>
    /// Per-feature contribution to the confluence score, signed by the decision's
    /// direction: positive means the feature supported the chosen side, negative
    /// means it voted against it. Keys are feature-family names ("RSI","MACD",
    /// "EMA","Bollinger","Volume","AlphaTrend","PMax","SR","Pattern","Regime").
    /// Only populated for candidates (ShouldTrade=true).
    /// </summary>
    public IReadOnlyDictionary<string, int> FeatureScores { get; init; } =
        new Dictionary<string, int>();
}
