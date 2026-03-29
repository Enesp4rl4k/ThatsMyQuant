using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ThatsMyQuant.Infrastructure.Services.Strategy;

/// <summary>
/// Enhanced HFT scalping strategy using 5-factor confluence:
///   1. Bollinger Squeeze — volatility compression precedes explosive moves
///   2. Order Flow Imbalance (OFI) — bid/ask pressure from real order book
///   3. Spread check — only trade when spread is tight (&lt; MaxSpreadBps)
///   4. Fill probability model — estimates likelihood of getting filled at target price
///   5. Tick momentum — FeatureEngineeringAgent's real-time RSI + EMA deviation
///
/// All inputs come from live tick data via FeatureEngineeringAgent and TickPipeline.
/// Scoring: base 5 per confirmed factor. Requires 3/5 minimum for a signal.
/// </summary>
public sealed class ScalpingStrategyAgent : IStrategyAgent
{
    private readonly ILogger<ScalpingStrategyAgent> _logger;
    private readonly ScalpingOptions _opts;

    public string StrategyTag => "Scalping";
    public int SwarmWeight => _opts.SwarmWeight;

    public ScalpingStrategyAgent(ILogger<ScalpingStrategyAgent> logger, IOptions<ScalpingOptions>? options = null)
    {
        _logger = logger;
        _opts = options?.Value ?? new ScalpingOptions();
    }

    public bool CanHandle(Signal signal, MarketRegime regime)
        => regime is MarketRegime.Volatile;

    public Task<StrategyVote> EvaluateAsync(
        Signal signal,
        PatternSnapshot patterns,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        // ── Factor 1: Bollinger Squeeze ──
        bool hasSqueeze = patterns.BollingerSqueeze;

        // ── Factor 2: Order Flow Imbalance direction ──
        double ofi = foundation.BidAskImbalance;
        bool hasStrongOfi = Math.Abs(ofi) >= _opts.StrongOfiThreshold;
        bool hasMinOfi = Math.Abs(ofi) >= _opts.MinOfiThreshold;
        bool ofiBullish = ofi > 0;

        // Need at least minimum OFI for direction — if no imbalance, nothing to scalp
        if (!hasMinOfi)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        // ── Factor 3: Spread check ──
        double spreadBps = ComputeSpreadBps(signal, foundation);
        bool spreadTight = spreadBps > 0 && spreadBps <= _opts.MaxSpreadBps;

        // ── Factor 4: Fill probability model ──
        double fillProb = EstimateFillProbability(ofi, spreadBps, ofiBullish, _opts.MaxSpreadBps);
        bool fillLikely = fillProb >= _opts.MinFillProbability;

        // ── Factor 5: Tick momentum from real-time features ──
        // Use VWAP deviation as momentum proxy (available from foundation tier)
        bool tickMomentumConfirmed = false;
        if (!double.IsNaN(patterns.VwapDeviationSigmas))
        {
            var absVwapDev = Math.Abs(patterns.VwapDeviationSigmas);
            // Momentum confirmed if VWAP deviation agrees with OFI direction
            // and is not extreme (< MaxVwapDeviation to avoid chasing)
            tickMomentumConfirmed = absVwapDev > 0.2 && absVwapDev < _opts.MaxVwapDeviation
                && ((ofiBullish && patterns.VwapDeviationSigmas < 0) // bullish: price below VWAP = buy dip
                    || (!ofiBullish && patterns.VwapDeviationSigmas > 0)); // bearish: price above VWAP = sell rip
        }

        // ── Confluence scoring ──
        int confirmedFactors = 0;
        var reasons = new List<string>(6);

        if (hasSqueeze)
        {
            confirmedFactors++;
            reasons.Add("BB squeeze");
        }

        if (hasStrongOfi)
        {
            confirmedFactors++;
            reasons.Add($"OFI={ofi:F2}");
        }
        else if (hasMinOfi)
        {
            // Weak OFI counts as half-factor (still counted but with less weight)
            reasons.Add($"OFI={ofi:F2} weak");
        }

        if (spreadTight)
        {
            confirmedFactors++;
            reasons.Add($"spread={spreadBps:F1}bps");
        }
        else
        {
            reasons.Add($"spread={spreadBps:F1}bps wide");
        }

        if (fillLikely)
        {
            confirmedFactors++;
            reasons.Add($"fill={fillProb:P0}");
        }

        if (tickMomentumConfirmed)
        {
            confirmedFactors++;
            reasons.Add("VWAP momentum");
        }

        // Need at least 3 confirmed factors
        if (confirmedFactors < 3)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        float confidence = confirmedFactors * _opts.BaseConfidencePerFactor;
        if (Math.Abs(ofi) > _opts.VeryStrongOfiThreshold) confidence += _opts.VeryStrongOfiBonus;
        if (spreadBps > 0 && spreadBps < _opts.TightSpreadBps) confidence += _opts.TightSpreadBonus;

        var reasoning = string.Join(" | ", reasons);
        bool isLong = ofiBullish;

        return Task.FromResult(isLong
            ? StrategyVote.Approve(StrategyTag, confidence,
                $"Scalp long ({confirmedFactors}/5 factors): {reasoning}", priorWeight: SwarmWeight)
            : StrategyVote.Reject(StrategyTag, confidence,
                $"Scalp short ({confirmedFactors}/5 factors): {reasoning}", priorWeight: SwarmWeight));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Analysis Methods
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes the bid-ask spread in basis points from either the signal's microstructure
    /// data (BestBid/BestAsk from order book) or the foundation tier's SpreadBps.
    /// </summary>
    internal static double ComputeSpreadBps(Signal signal, FoundationSnapshot foundation)
    {
        // Prefer real-time order book data from the signal
        if (signal.BestBid.HasValue && signal.BestAsk.HasValue
            && signal.BestBid.Value > 0 && signal.BestAsk.Value > 0)
        {
            // Crossed book (BestBid >= BestAsk) = unsafe to scalp
            if (signal.BestAsk.Value <= signal.BestBid.Value)
                return double.MaxValue;

            double mid = (signal.BestBid.Value + signal.BestAsk.Value) / 2.0;
            if (mid > 0)
                return (signal.BestAsk.Value - signal.BestBid.Value) / mid * 10_000;
        }

        // Fall back to foundation tier's pre-computed spread
        if (foundation.SpreadBps > 0)
            return foundation.SpreadBps;

        return double.MaxValue; // unknown spread = unsafe to scalp
    }

    /// <summary>
    /// Estimates fill probability based on:
    ///  - OFI alignment: strong same-direction flow means queue is moving your way
    ///  - Spread tightness: tighter spread = more activity at best levels = better fills
    ///  - Direction: buying into strong bid imbalance (or selling into ask) = favorable queue
    ///
    /// Returns 0.0 to 1.0.
    /// </summary>
    internal static double EstimateFillProbability(double ofi, double spreadBps, bool isLong, double maxSpreadBps = 10.0)
    {
        double absOfi = Math.Abs(ofi);
        double ofiComponent = Math.Min(absOfi, 1.0) * 0.5;

        bool aligned = (isLong && ofi > 0) || (!isLong && ofi < 0);
        double alignmentBonus = aligned ? 0.2 : -0.1;

        double spreadComponent = 0;
        if (spreadBps > 0 && spreadBps < double.MaxValue)
        {
            spreadComponent = Math.Max(0, 0.3 * (1.0 - spreadBps / maxSpreadBps));
        }

        return Math.Clamp(ofiComponent + alignmentBonus + spreadComponent, 0.0, 1.0);
    }
}
