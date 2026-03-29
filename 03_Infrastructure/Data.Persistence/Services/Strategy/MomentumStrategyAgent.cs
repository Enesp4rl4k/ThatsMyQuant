using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.Strategy;

public sealed class MomentumStrategyAgent : IStrategyAgent
{
    private readonly ILogger<MomentumStrategyAgent> _logger;
    private const int RsiPeriod = 14;
    private const double RsiBullThreshold = 55.0;
    private const double RsiBearThreshold = 45.0;

    public string StrategyTag => "Momentum";
    public int SwarmWeight => 20;

    public MomentumStrategyAgent(ILogger<MomentumStrategyAgent> logger)
        => _logger = logger;

    public bool CanHandle(Signal signal, MarketRegime regime)
        => regime is MarketRegime.Trending;

    public Task<StrategyVote> EvaluateAsync(
        Signal signal,
        PatternSnapshot patterns,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        // Need enough candles for MACD (26 + 9) and RSI stabilization (at least 50 is safe)
        if (candles.Count < 60)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var closes = candles.Select(c => (double)c.Close).ToList();
        
        var rsi = ComputeWildersRsi(closes, RsiPeriod);
        var (macdLine, signalLine, histogram) = ComputeMacd(closes);

        var volLast3Avg = candles.TakeLast(3).Average(c => c.Volume);
        var volPrev20Avg = candles.TakeLast(23).Take(20).Average(c => c.Volume);
        var volRatio = volPrev20Avg > 0 ? (double)(volLast3Avg / volPrev20Avg) : 1.0;

        bool priceAboveVwap = foundation.Vwap > 0 && signal.Price > foundation.Vwap;

        // Momentum persistence: 3 consecutive closes in trend direction
        bool threeWhiteSoldiers = closes[^1] > closes[^2] && closes[^2] > closes[^3] && closes[^3] > closes[^4];
        bool threeBlackCrows = closes[^1] < closes[^2] && closes[^2] < closes[^3] && closes[^3] < closes[^4];

        if (rsi > 55 && priceAboveVwap && volRatio > 1.2 && threeWhiteSoldiers && histogram > 0)
        {
            return Task.FromResult(StrategyVote.Approve(StrategyTag, 20f,
                $"RSI={rsi:F1} bullish + VWAP + VolRatio={volRatio:F1} + 3xCloses + MACD Hist>0"));
        }

        if (rsi < 45 && !priceAboveVwap && volRatio > 1.2 && threeBlackCrows && histogram < 0)
        {
            return Task.FromResult(StrategyVote.Reject(StrategyTag, 20f,
                $"RSI={rsi:F1} bearish + VWAP + VolRatio={volRatio:F1} + 3xCloses + MACD Hist<0"));
        }

        return Task.FromResult(StrategyVote.Abstain(StrategyTag));
    }

    private static double ComputeWildersRsi(IReadOnlyList<double> closes, int period)
    {
        if (closes.Count <= period) return 50.0;

        double sumGain = 0;
        double sumLoss = 0;

        for (int i = 1; i <= period; i++)
        {
            double diff = closes[i] - closes[i - 1];
            if (diff >= 0) sumGain += diff;
            else sumLoss -= diff;
        }

        double avgGain = sumGain / period;
        double avgLoss = sumLoss / period;

        for (int i = period + 1; i < closes.Count; i++)
        {
            double diff = closes[i] - closes[i - 1];
            double gain = diff >= 0 ? diff : 0;
            double loss = diff < 0 ? -diff : 0;

            avgGain = (avgGain * (period - 1) + gain) / period;
            avgLoss = (avgLoss * (period - 1) + loss) / period;
        }

        if (Math.Abs(avgLoss) < 1e-9) return 100.0;
        double rs = avgGain / avgLoss;
        return 100.0 - (100.0 / (1.0 + rs));
    }

    private static (double Macd, double Signal, double Histogram) ComputeMacd(IReadOnlyList<double> closes)
    {
        var ema12 = ComputeEma(closes, 12);
        var ema26 = ComputeEma(closes, 26);
        
        var macdSeries = new List<double>(closes.Count);
        for(int i = 0; i < closes.Count; i++)
        {
            macdSeries.Add(ema12[i] - ema26[i]);
        }
        
        var signalSeries = ComputeEma(macdSeries, 9);
        var finalMacd = macdSeries[^1];
        var finalSignal = signalSeries[^1];
        var finalHist = finalMacd - finalSignal;
        
        return (finalMacd, finalSignal, finalHist);
    }
    
    private static List<double> ComputeEma(IReadOnlyList<double> values, int period)
    {
        var result = new List<double>(values.Count);
        double k = 2.0 / (period + 1);
        double currentEma = values[0]; // Seed with first value
        result.Add(currentEma);
        
        for (int i = 1; i < values.Count; i++)
        {
            currentEma = (values[i] - currentEma) * k + currentEma;
            result.Add(currentEma);
        }
        return result;
    }
}
