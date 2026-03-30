using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Infrastructure.Services.Strategy;

public sealed class StatisticalArbitrageAgent : IStrategyAgent
{
    private readonly ILogger<StatisticalArbitrageAgent> _logger;
    private readonly StatArbOptions _options;

    public string StrategyTag => "StatArb";
    public int SwarmWeight => _options.SwarmWeight;

    public StatisticalArbitrageAgent(ILogger<StatisticalArbitrageAgent> logger, IOptions<StatArbOptions>? options = null)
    {
        _logger = logger;
        _options = options?.Value ?? new StatArbOptions();
    }

    public bool CanHandle(Signal signal, MarketRegime regime)
        => regime is MarketRegime.Ranging;

    public Task<StrategyVote> EvaluateAsync(
        Signal signal,
        PatternSnapshot patterns,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        if (candles.Count < _options.LookbackBars)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var closes = candles.TakeLast(_options.LookbackBars).Select(c => (double)c.Close).ToArray();
        var mean = closes.Average();
        var variance = closes.Select(c => (c - mean) * (c - mean)).Average();
        var stdDev = Math.Sqrt(variance);

        if (stdDev < 1e-10 || Math.Abs(mean) < 1e-10)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var zScore = ((double)signal.Price - mean) / stdDev;
        if (Math.Abs(zScore) < _options.EntryZScore)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var coefficientOfVariation = stdDev / Math.Abs(mean);
        var hasMeaningfulDispersion = coefficientOfVariation >= _options.MinCoefficientOfVariation;

        var halfLife = EstimateHalfLife(closes);
        var hasGoodHalfLife = halfLife > 0 && halfLife <= _options.MaxHalfLife;
        var hasFastHalfLife = halfLife > 0 && halfLife <= _options.FastHalfLife;

        var adfStatistic = ComputeAdfStatistic(closes);
        var isStationary = adfStatistic < _options.AdfCriticalValue;

        var confirmedFactors = 1;
        var reasons = new List<string> { $"z={zScore:F2}" };

        if (hasMeaningfulDispersion)
        {
            confirmedFactors++;
            reasons.Add($"CV={coefficientOfVariation:P2}");
        }
        else
        {
            reasons.Add($"CV={coefficientOfVariation:P2} low");
        }

        if (hasGoodHalfLife)
        {
            confirmedFactors++;
            reasons.Add($"HL={halfLife:F1} bars");
        }
        else
        {
            reasons.Add(halfLife <= 0 ? "HL=none" : $"HL={halfLife:F1} slow");
        }

        if (isStationary)
        {
            confirmedFactors++;
            reasons.Add($"ADF={adfStatistic:F2} stationary");
        }
        else
        {
            reasons.Add($"ADF={adfStatistic:F2} non-stationary");
        }

        if (confirmedFactors < 3)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        float confidence = confirmedFactors * _options.BaseConfidencePerFactor;
        confidence += (float)(Math.Min(Math.Abs(zScore) - _options.EntryZScore, _options.ZScoreMagnitudeCap) * _options.ZScoreBonusMultiplier);
        if (hasFastHalfLife) confidence += _options.FastHalfLifeBonus;
        if (adfStatistic < _options.AdfCriticalValue - _options.AdfBonusOffset) confidence += _options.StrongStationarityBonus;

        if (!double.IsNaN(patterns.VwapDeviationSigmas))
        {
            var vwapAgrees = (zScore > 0 && patterns.VwapDeviationSigmas > _options.VwapAgreementThreshold)
                || (zScore < 0 && patterns.VwapDeviationSigmas < -_options.VwapAgreementThreshold);

            if (vwapAgrees)
            {
                confidence += _options.VwapAgreementBonus;
                reasons.Add($"VWAP {patterns.VwapDeviationSigmas:F1}sig agrees");
            }
        }

        var direction = zScore > 0 ? "short" : "long";
        var reasoning = string.Join(" | ", reasons);
        return Task.FromResult(StrategyVote.Approve(StrategyTag, confidence, $"StatArb {direction} ({confirmedFactors}/4 factors): {reasoning}"));
    }

    private static double EstimateHalfLife(double[] prices)
    {
        if (prices.Length < 10) return -1;

        var mean = prices.Average();
        double sumXY = 0;
        double sumXX = 0;

        for (var i = 1; i < prices.Length; i++)
        {
            var x = prices[i - 1] - mean;
            var y = prices[i] - prices[i - 1];
            sumXY += x * y;
            sumXX += x * x;
        }

        if (sumXX < 1e-15) return -1;

        var lambda = sumXY / sumXX;
        if (lambda >= 0) return -1;

        return -Math.Log(2) / lambda;
    }

    private static double ComputeAdfStatistic(double[] prices)
    {
        if (prices.Length < 10) return 0;

        var n = prices.Length - 1;
        double sumX = 0;
        double sumY = 0;
        double sumXX = 0;
        double sumXY = 0;

        for (var i = 1; i < prices.Length; i++)
        {
            var x = prices[i - 1];
            var y = prices[i] - prices[i - 1];
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumXY += x * y;
        }

        var meanX = sumX / n;
        var meanY = sumY / n;
        var sxx = sumXX - n * meanX * meanX;
        if (Math.Abs(sxx) < 1e-15) return 0;

        var sxy = sumXY - n * meanX * meanY;
        var beta = sxy / sxx;
        var alpha = meanY - beta * meanX;

        double sse = 0;
        for (var i = 1; i < prices.Length; i++)
        {
            var x = prices[i - 1];
            var y = prices[i] - prices[i - 1];
            var residual = y - alpha - beta * x;
            sse += residual * residual;
        }

        var mse = sse / (n - 2);
        if (mse < 1e-15) return 0;

        var seBeta = Math.Sqrt(mse / sxx);
        if (seBeta < 1e-15) return 0;

        return beta / seBeta;
    }
}
