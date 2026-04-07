using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Infrastructure.Services.Strategy;

public sealed class ScalpingStrategyAgent : IStrategyAgent
{
    private readonly ILogger<ScalpingStrategyAgent> _logger;
    private readonly ScalpingOptions _options;
    private readonly StrategyTuningService? _tuningService;

    public string StrategyTag => "Scalping";
    public int SwarmWeight => ResolveOptions().SwarmWeight;

    public ScalpingStrategyAgent(
        ILogger<ScalpingStrategyAgent> logger,
        IOptions<ScalpingOptions>? options = null,
        StrategyTuningService? tuningService = null)
    {
        _logger = logger;
        _options = options?.Value ?? new ScalpingOptions();
        _tuningService = tuningService;
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
        var options = ResolveOptions();
        var hasSqueeze = patterns.BollingerSqueeze;
        var ofi = foundation.BidAskImbalance;
        var absOfi = Math.Abs(ofi);
        if (absOfi < options.MinOfiThreshold)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var spreadBps = ComputeSpreadBps(signal, foundation);
        var spreadTight = spreadBps > 0 && spreadBps <= options.MaxSpreadBps;
        var fillProbability = EstimateFillProbability(ofi, spreadBps, ofi > 0, options.MaxSpreadBps);
        var fillLikely = fillProbability >= options.MinFillProbability;

        var tickMomentumConfirmed = false;
        if (!double.IsNaN(patterns.VwapDeviationSigmas))
        {
            var absDeviation = Math.Abs(patterns.VwapDeviationSigmas);
            tickMomentumConfirmed = absDeviation > 0.2
                && absDeviation < options.MaxVwapDeviation
                && ((ofi > 0 && patterns.VwapDeviationSigmas < 0) || (ofi < 0 && patterns.VwapDeviationSigmas > 0));
        }

        var confirmedFactors = 0;
        var reasons = new List<string>();

        if (hasSqueeze)
        {
            confirmedFactors++;
            reasons.Add("BB squeeze");
        }

        if (absOfi >= options.StrongOfiThreshold)
        {
            confirmedFactors++;
            reasons.Add($"OFI={ofi:F2}");
        }
        else
        {
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
            reasons.Add($"fill={fillProbability:P0}");
        }

        if (tickMomentumConfirmed)
        {
            confirmedFactors++;
            reasons.Add("VWAP momentum");
        }

        if (confirmedFactors < 3)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        float confidence = confirmedFactors * options.BaseConfidencePerFactor;
        if (absOfi > options.VeryStrongOfiThreshold) confidence += options.VeryStrongOfiBonus;
        if (spreadBps > 0 && spreadBps < options.TightSpreadBps) confidence += options.TightSpreadBonus;

        var reasoning = string.Join(" | ", reasons);
        return Task.FromResult(ofi > 0
            ? StrategyVote.Approve(StrategyTag, confidence, $"Scalp long ({confirmedFactors}/5 factors): {reasoning}")
            : StrategyVote.Reject(StrategyTag, confidence, $"Scalp short ({confirmedFactors}/5 factors): {reasoning}"));
    }

    private ScalpingOptions ResolveOptions()
    {
        return _tuningService?.GetScalpingOptions() ?? _options;
    }

    private static double ComputeSpreadBps(Signal signal, FoundationSnapshot foundation)
    {
        if (signal.BestBid.HasValue && signal.BestAsk.HasValue &&
            signal.BestBid.Value > 0 && signal.BestAsk.Value > 0)
        {
            if (signal.BestAsk.Value <= signal.BestBid.Value)
                return double.MaxValue;

            var mid = (signal.BestBid.Value + signal.BestAsk.Value) / 2.0;
            if (mid > 0)
                return (signal.BestAsk.Value - signal.BestBid.Value) / mid * 10_000;
        }

        return foundation.SpreadBps > 0 ? foundation.SpreadBps : double.MaxValue;
    }

    private static double EstimateFillProbability(double ofi, double spreadBps, bool isLong, double maxSpreadBps)
    {
        var absOfi = Math.Min(Math.Abs(ofi), 1.0);
        var ofiComponent = absOfi * 0.5;
        var aligned = (isLong && ofi > 0) || (!isLong && ofi < 0);
        var alignmentBonus = aligned ? 0.2 : -0.1;

        var spreadComponent = 0.0;
        if (spreadBps > 0 && spreadBps < double.MaxValue)
            spreadComponent = Math.Max(0, 0.3 * (1.0 - spreadBps / maxSpreadBps));

        return Math.Clamp(ofiComponent + alignmentBonus + spreadComponent, 0.0, 1.0);
    }
}
