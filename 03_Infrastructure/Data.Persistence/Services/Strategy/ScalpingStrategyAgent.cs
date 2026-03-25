using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.Strategy;

public sealed class ScalpingStrategyAgent : IStrategyAgent
{
    private readonly ILogger<ScalpingStrategyAgent> _logger;
    private const double MinOFIImbalance = 0.25;
    private const double MaxVwapDeviation = 1.5;

    public string StrategyTag => "Scalping";
    public int SwarmWeight => 15;

    public ScalpingStrategyAgent(ILogger<ScalpingStrategyAgent> logger)
        => _logger = logger;

    public bool CanHandle(Signal signal, MarketRegime regime)
        => regime is MarketRegime.Volatile; // User said HFT signals, typically volatile

    public Task<StrategyVote> EvaluateAsync(
        Signal signal,
        PatternSnapshot patterns,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        if (!patterns.BollingerSqueeze)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        if (Math.Abs(foundation.BidAskImbalance) < 0.6)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var isLong = foundation.BidAskImbalance > 0.6;
        var reason = $"Scalp: Squeeze release + OFI={foundation.BidAskImbalance:F2}";

        return Task.FromResult(isLong
            ? StrategyVote.Approve(StrategyTag, 15f, reason)
            : StrategyVote.Reject(StrategyTag, 15f, reason));
    }
}
