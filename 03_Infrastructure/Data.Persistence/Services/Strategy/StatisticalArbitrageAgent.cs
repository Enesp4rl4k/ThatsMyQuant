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

        var closes = candles.TakeLast(LookbackBars).Select(c => (double)c.Close).ToList();
        var mean = closes.Average();
        var variance = closes.Select(c => Math.Pow(c - mean, 2)).Average();
        var stdDev = Math.Sqrt(variance);

        if (stdDev < 0.0001)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var zScore = ((double)signal.Price - mean) / stdDev;

        if (Math.Abs(zScore) > EntryZScore)
        {
            var direction = zScore > 0 ? "short" : "long";
            var adjustment = (float)(Math.Min(Math.Abs(zScore) - EntryZScore, 2.0) * 5f);
            return Task.FromResult(StrategyVote.Approve(StrategyTag, adjustment,
                $"StatArb z-score={zScore:F2} — mean-reversion {direction} setup, VWAP dev={patterns.VwapDeviationSigmas:F2}σ"));
        }

        return Task.FromResult(StrategyVote.Abstain(StrategyTag));
    }
}
