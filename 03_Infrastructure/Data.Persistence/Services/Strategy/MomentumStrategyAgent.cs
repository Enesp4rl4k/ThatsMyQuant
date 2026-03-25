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
        if (candles.Count < RsiPeriod + 1)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var rsi = ComputeRsi(candles);
        var volLast3Avg = candles.TakeLast(3).Average(c => c.Volume);
        var volPrev20Avg = candles.TakeLast(23).Take(20).Average(c => c.Volume);
        var volRatio = volPrev20Avg > 0 ? (double)(volLast3Avg / volPrev20Avg) : 1.0;

        bool priceAboveVwap = foundation.Vwap > 0 && signal.Price > foundation.Vwap;

        if (rsi > 55 && priceAboveVwap && volRatio > 1.2)
        {
            return Task.FromResult(StrategyVote.Approve(StrategyTag, 20f,
                $"RSI={rsi:F1} bullish + Price > VWAP + VolRatio={volRatio:F1} expansion"));
        }

        if (rsi < 45 && !priceAboveVwap && volRatio > 1.2)
        {
            return Task.FromResult(StrategyVote.Reject(StrategyTag, 20f,
                $"RSI={rsi:F1} bearish + Price < VWAP + VolRatio={volRatio:F1} expansion"));
        }

        return Task.FromResult(StrategyVote.Abstain(StrategyTag));
    }

    private static double ComputeRsi(IReadOnlyList<Candle> candles)
    {
        var closes = candles.TakeLast(RsiPeriod + 1).Select(c => c.Close).ToList();
        double gains = 0, losses = 0;
        for (int i = 1; i < closes.Count; i++)
        {
            var diff = (double)(closes[i] - closes[i - 1]);
            if (diff > 0) gains += diff;
            else losses -= diff;
        }
        if (losses == 0) return 100.0;
        var rs = gains / losses;
        return 100.0 - (100.0 / (1 + rs));
    }

    private static bool IsVolumeExpanding(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 5) return false;
        var last3Avg = candles.TakeLast(3).Average(c => c.Volume);
        var prev5Avg = candles.TakeLast(8).Take(5).Average(c => c.Volume);
        return prev5Avg > 0 && last3Avg > prev5Avg * 1.1m;
    }
}
