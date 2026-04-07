using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ThatsMyQuant.Infrastructure.Services.Risk;

public sealed class CorrelationGuardAgent : IRiskGuardAgent
{
    private readonly ILogger<CorrelationGuardAgent> _logger;
    private readonly int _maxSameSymbolPositions;
    private readonly double _highCorrelationThreshold;

    private static readonly HashSet<HashSet<string>> KnownCorrelatedGroups =
    [
        new(StringComparer.OrdinalIgnoreCase) { "BTCUSDT", "ETHUSDT", "SOLUSDT" },
        new(StringComparer.OrdinalIgnoreCase) { "XAUUSD", "XAGUSD" },
        new(StringComparer.OrdinalIgnoreCase) { "EURUSD", "GBPUSD", "AUDUSD" },
    ];

    public string GuardId => "Risk.CorrelationGuard";
    public int Order => 2;

    public CorrelationGuardAgent(ILogger<CorrelationGuardAgent> logger, IOptions<RiskGuardAgentOptions>? options = null)
    {
        _logger = logger;
        var opts = options?.Value ?? new RiskGuardAgentOptions();
        _maxSameSymbolPositions = opts.MaxSameSymbolPositions;
        _highCorrelationThreshold = opts.HighCorrelationThreshold;
    }

    public Task<RiskGuardResult> EvaluateAsync(
        Signal signal,
        TradeDecision decision,
        IReadOnlyList<Trade> openTrades,
        CancellationToken ct = default)
    {
        var correlatedTrades = openTrades.Where(t => 
            IsCorrelated(t.Symbol, signal.Symbol)).ToList();

        var correlatedExhibition = correlatedTrades.Sum(t => t.PositionSizePercent);
        
        if (correlatedExhibition + decision.PositionSizePercent > 0.30m)
        {
            return Task.FromResult(RiskGuardResult.Block(GuardId, SignalReasonFlags.CorrelationGuardVeto,
                $"Total correlated exposure ({correlatedExhibition + decision.PositionSizePercent:P0}) exceeds 30% limit"));
        }

        return Task.FromResult(RiskGuardResult.Pass(GuardId));
    }

    private static bool IsCorrelated(string s1, string s2)
    {
        if (string.Equals(s1, s2, StringComparison.OrdinalIgnoreCase)) return true;
        
        foreach (var group in KnownCorrelatedGroups)
        {
            if (group.Contains(s1) && group.Contains(s2)) return true;
        }
        return false;
    }
}
