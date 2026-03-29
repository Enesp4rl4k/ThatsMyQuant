using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.Strategy;

public sealed class StatisticalArbitrageAgent : IStrategyAgent
{
    private readonly ILogger<StatisticalArbitrageAgent> _logger;
    private const int LookbackBars = 50;
    private const double EntryZScore = 2.0;

    public string StrategyTag => "StatArb";
    public int SwarmWeight => 30;

    public StatisticalArbitrageAgent(ILogger<StatisticalArbitrageAgent> logger)
        => _logger = logger;

    public bool CanHandle(Signal signal, MarketRegime regime)
        => regime is MarketRegime.Ranging;

    public Task<StrategyVote> EvaluateAsync(
        Signal signal,
        PatternSnapshot patterns,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        if (candles.Count < LookbackBars)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        // Use up to 100 historical candles for statistical sampling
        var maxSampleLength = Math.Min(candles.Count, 100);
        var closes = candles.TakeLast(maxSampleLength).Select(c => (double)c.Close).ToList();

        // 1. Calculate Ornstein-Uhlenbeck Mean Reversion Half-Life
        var halfLife = CalculateOUHalfLife(closes);
        if (double.IsNaN(halfLife) || halfLife < 1)
        {
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));
        }

        // 2. Validate Mean Reverting Nature via Hurst Exponent (H < 0.5)
        var hurst = CalculateHurstExponent(closes);
        if (hurst >= 0.5) 
        {
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));
        }

        // 3. Adaptive Lookback based on OU half-life
        int adaptiveLookback = Math.Clamp((int)Math.Round(halfLife * 2), 10, maxSampleLength);
        
        var recentCloses = closes.TakeLast(adaptiveLookback).ToList();
        var mean = recentCloses.Average();
        var variance = recentCloses.Select(c => Math.Pow(c - mean, 2)).Average();
        var stdDev = Math.Sqrt(variance);

        if (stdDev < 0.0001)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var zScore = ((double)signal.Price - mean) / stdDev;

        if (Math.Abs(zScore) > EntryZScore)
        {
            var direction = zScore > 0 ? "short" : "long";
            var adjustment = (float)(Math.Min(Math.Abs(zScore) - EntryZScore, 2.0) * 5f);
            return Task.FromResult(StrategyVote.Approve(StrategyTag, adjustment,
                $"StatArb z={zScore:F2} (OU λ={halfLife:F1}, H={hurst:F2}, win={adaptiveLookback}) — {direction}"));
        }

        return Task.FromResult(StrategyVote.Abstain(StrategyTag));
    }

    private static double CalculateOUHalfLife(IReadOnlyList<double> prices)
    {
        if (prices.Count < 10) return double.NaN;
        
        int n = prices.Count - 1;
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        
        for(int i = 0; i < n; i++)
        {
            double x = prices[i];
            double y = prices[i+1] - prices[i]; // delta P
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumXX += x * x;
        }
        
        double meanX = sumX / n;
        double meanY = sumY / n;
        
        double covXY = (sumXY / n) - (meanX * meanY);
        double varX = (sumXX / n) - (meanX * meanX);
        
        if (varX <= 1e-10) return double.NaN;
        
        double b = covXY / varX;
        
        // If b >= 0, series is not mean-reverting (it's either a random walk or trending)
        if (b >= -1e-5) return double.NaN; 
        
        return -Math.Log(2) / b;
    }

    private static double CalculateHurstExponent(IReadOnlyList<double> prices)
    {
        if (prices.Count < 10) return 0.5;
        
        int n = prices.Count;
        double mean = prices.Average();
        
        double minZ = 0, maxZ = 0, z = 0;
        double sumSq = 0;
        
        for (int i = 0; i < n; i++)
        {
            double y = prices[i] - mean;
            z += y;
            if (z > maxZ) maxZ = z;
            if (z < minZ) minZ = z;
            sumSq += y * y;
        }
        
        double r = maxZ - minZ;
        double s = Math.Sqrt(sumSq / n);
        
        if (s <= 1e-10 || r <= 1e-10) return 0.5;
        
        return Math.Log(r / s) / Math.Log(n);
    }
}
