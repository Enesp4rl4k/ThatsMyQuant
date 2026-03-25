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

        var avgVol = candles.TakeLast(20).Average(c => c.Volume);
        var lastCandle = candles[^1];
        var volumeSurge = avgVol > 0 && lastCandle.Volume > avgVol * 2m;

        // Fib level 0.786 breakout check
        var swingHigh = candles.TakeLast(50).Max(c => c.High);
        var swingLow = candles.TakeLast(50).Min(c => c.Low);
        var fib786 = swingLow + (swingHigh - swingLow) * 0.786m;

        if (signal.Price > fib786 && volumeSurge)
        {
            return Task.FromResult(StrategyVote.Approve(StrategyTag, 25f,
                $"Breakout above 0.786 Fib level {fib786:F2} with 2x volume surge"));
        }

        return Task.FromResult(StrategyVote.Abstain(StrategyTag));
    }
}
