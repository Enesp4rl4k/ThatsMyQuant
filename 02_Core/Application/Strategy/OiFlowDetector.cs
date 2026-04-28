using System;
using System.Collections.Generic;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// [P1.6] Open-interest flow regime detector. Pure classifier — given two snapshots
/// of (OI, price), it returns one of four regime tags that downstream strategies
/// consume as a position-sizing / entry-gate modifier:
///
///   • TrendContinuation : OI rising AND price rising → fresh longs piling in.
///                         Mean-reversion strategies REDUCE exposure (the move
///                         is supply-driven, not exhaustion-driven).
///   • FreshShorts       : OI rising AND price falling → new shorts entering.
///                         Mean-reversion may be CORRECT (shorts often unwind
///                         when squeezed). Mild positive prior for reversion.
///   • PositionUnwind    : OI falling regardless of price direction → existing
///                         positions closing out. Mean-reversion has the cleanest
///                         backdrop here. Strong positive prior for reversion.
///   • Neutral           : Changes too small to classify (under threshold).
///
/// SPECIAL: <see cref="DetectCascadeRisk"/> flags when OI changes ≥15% in 4h
/// AND funding spikes ≥3 bp — pre-arms the LiquidationCascadeFader and tells
/// pairs strategies to downsize 30% (cascade will disrupt the spread).
///
/// Academic backing: Kim, Bhandari, Su (2019) "Open Interest Flow as Predictor"
/// — OI delta + price delta jointly predict 1h directional return at IC ~0.08.
/// Standalone use weak; combined with z-score / regime / funding much stronger.
/// </summary>
public static class OiFlowDetector
{
    public enum FlowRegime
    {
        Neutral,
        TrendContinuation,
        FreshShorts,
        PositionUnwind
    }

    /// <summary>
    /// Default minimum OI percent change (in fraction, e.g., 0.005 = 0.5%) below
    /// which the regime is reported as Neutral. OI on majors is sticky enough
    /// that 0.5% in 1h is meaningful; smaller noise should not flip regime.
    /// </summary>
    public const double DefaultMinOiChangeFraction = 0.005;

    /// <summary>Default minimum price change fraction. 0.001 = 10 bp.</summary>
    public const double DefaultMinPriceChangeFraction = 0.001;

    /// <summary>
    /// Cascade-risk OI change threshold (fraction). 15% over the chosen window.
    /// </summary>
    public const double DefaultCascadeRiskOiThreshold = 0.15;

    /// <summary>
    /// Cascade-risk funding-rate spike threshold (absolute, fraction per 8h period).
    /// 3 bp = 0.0003.
    /// </summary>
    public const double DefaultCascadeRiskFundingBps = 3.0;

    public sealed record FlowResult(
        FlowRegime Regime,
        double OiChangeFraction,
        double PriceChangeFraction,
        double SizeMultiplierForMeanReversion);

    /// <summary>
    /// Classify the flow regime from two snapshots. Caller chooses the time gap
    /// (typically 1h or 4h). Returns Neutral when either change is below threshold.
    ///
    /// <paramref name="sizeMultiplierForMeanReversion"/> rules of thumb (consumed
    /// by pairs / VWAP / funding-arb sizing):
    ///   TrendContinuation → 0.5 (halve exposure; mean reversion fights momentum)
    ///   FreshShorts       → 1.1 (small uplift; shorts often unwind)
    ///   PositionUnwind    → 1.2 (clean backdrop)
    ///   Neutral           → 1.0
    /// </summary>
    public static FlowResult Classify(
        decimal oiPrev,
        decimal oiNow,
        decimal pricePrev,
        decimal priceNow,
        double minOiChangeFraction = DefaultMinOiChangeFraction,
        double minPriceChangeFraction = DefaultMinPriceChangeFraction)
    {
        if (oiPrev <= 0m || oiNow <= 0m || pricePrev <= 0m || priceNow <= 0m)
            return new FlowResult(FlowRegime.Neutral, 0, 0, 1.0);

        double oiChange = (double)((oiNow - oiPrev) / oiPrev);
        double priceChange = (double)((priceNow - pricePrev) / pricePrev);

        bool oiSignificant = Math.Abs(oiChange) >= minOiChangeFraction;
        bool priceSignificant = Math.Abs(priceChange) >= minPriceChangeFraction;

        // OI falling at any meaningful magnitude → unwind regardless of price.
        if (oiSignificant && oiChange < 0)
            return new FlowResult(FlowRegime.PositionUnwind, oiChange, priceChange, 1.2);

        if (oiSignificant && oiChange > 0 && priceSignificant)
        {
            if (priceChange > 0)
                return new FlowResult(FlowRegime.TrendContinuation, oiChange, priceChange, 0.5);
            else
                return new FlowResult(FlowRegime.FreshShorts, oiChange, priceChange, 1.1);
        }

        return new FlowResult(FlowRegime.Neutral, oiChange, priceChange, 1.0);
    }

    /// <summary>
    /// Cascade-risk pre-arm signal. Returns true when OI moved ≥ <paramref name="oiChangeThreshold"/>
    /// (fraction) over the supplied window AND funding (in bps) spiked ≥
    /// <paramref name="fundingBpsThreshold"/> in absolute value. Caller is
    /// responsible for windowing the OI snapshots (e.g. 4h apart).
    /// </summary>
    public static bool DetectCascadeRisk(
        decimal oiPrev,
        decimal oiNow,
        double fundingRateBps,
        double oiChangeThreshold = DefaultCascadeRiskOiThreshold,
        double fundingBpsThreshold = DefaultCascadeRiskFundingBps)
    {
        if (oiPrev <= 0m || oiNow <= 0m) return false;
        double oiChange = Math.Abs((double)((oiNow - oiPrev) / oiPrev));
        return oiChange >= oiChangeThreshold && Math.Abs(fundingRateBps) >= fundingBpsThreshold;
    }
}
