using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.Strategy;

public sealed class BreakoutStrategyAgent : IStrategyAgent
{
    private readonly ILogger<BreakoutStrategyAgent> _logger;
    private const int ConsolidationBars = 20;
    private const double VolumeSurgeMultiple = 1.5;

    public string StrategyTag => "Breakout";
    public int SwarmWeight => 25;

    public BreakoutStrategyAgent(ILogger<BreakoutStrategyAgent> logger)
        => _logger = logger;

    public bool CanHandle(Signal signal, MarketRegime regime)
        => regime is MarketRegime.Volatile;

    public Task<StrategyVote> EvaluateAsync(
        Signal signal,
        PatternSnapshot patterns,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        if (candles.Count < ConsolidationBars + 3)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        // Time-of-day Liquidity Filter: Abstain during dead-zone handover (23:00-01:00 UTC)
        int hour = signal.Timestamp.Hour;
        if (hour == 23 || hour == 0)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var avgVol = candles.TakeLast(20).Average(c => c.Volume);
        var lastCandle = candles[^1];
        
        // Use declared constant instead of hardcoded 2m
        var volumeSurge = avgVol > 0 && lastCandle.Volume > avgVol * (decimal)VolumeSurgeMultiple;

        if (!volumeSurge)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var swingHigh = candles.TakeLast(50).Max(c => c.High);
        var swingLow = candles.TakeLast(50).Min(c => c.Low);
        
        var fib786 = swingLow + (swingHigh - swingLow) * 0.786m;
        var fib214 = swingLow + (swingHigh - swingLow) * 0.214m;

        if (signal.Side == TradeSide.Buy && signal.Price > fib786)
        {
            // Order Book Absorption Check: Positive Bid/Ask Imbalance confirms buying pressure
            if (foundation.BidAskImbalance < 0.1)
                return Task.FromResult(StrategyVote.Abstain(StrategyTag));

            return Task.FromResult(StrategyVote.Approve(StrategyTag, 25f,
                $"Long Breakout above 0.786 Fib ({fib786:F2}) with {VolumeSurgeMultiple}x volume surge, OFI={foundation.BidAskImbalance:F2}"));
        }
        
        if (signal.Side == TradeSide.Sell && signal.Price < fib214)
        {
            // Order Book Absorption Check: Negative Bid/Ask Imbalance confirms selling pressure
            if (foundation.BidAskImbalance > -0.1)
                return Task.FromResult(StrategyVote.Abstain(StrategyTag));

            return Task.FromResult(StrategyVote.Approve(StrategyTag, 25f,
                $"Short Breakdown below 0.214 Fib ({fib214:F2}) with {VolumeSurgeMultiple}x volume surge, OFI={foundation.BidAskImbalance:F2}"));
        }

        return Task.FromResult(StrategyVote.Abstain(StrategyTag));
    }
}
