using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.Risk;

public sealed class PortfolioOptimiserAgent : IRiskGuardAgent
{
    private readonly ILogger<PortfolioOptimiserAgent> _logger;
    private const int MaxOpenTrades = 8;
    private const decimal MaxPortfolioHeat = 0.15m;

    public string GuardId => "Risk.PortfolioOptimiser";
    public int Order => 1;

    public PortfolioOptimiserAgent(ILogger<PortfolioOptimiserAgent> logger)
        => _logger = logger;

    public Task<RiskGuardResult> EvaluateAsync(
        Signal signal,
        TradeDecision decision,
        IReadOnlyList<Trade> openTrades,
        CancellationToken ct = default)
    {
        // Simple Sharpe-based block: if last 10 trades have Sharpe < 1.0, reduce size or block
        var recentHistory = openTrades.OrderByDescending(t => t.ExitTime).Take(10).ToList();
        if (recentHistory.Count >= 5)
        {
            var pnls = recentHistory.Select(t => (double)t.RealisedPnL).ToList();
            var avg = pnls.Average();
            var std = Math.Sqrt(pnls.Select(p => Math.Pow(p - avg, 2)).Average());
            var sharpe = std > 0 ? (avg / std) * Math.Sqrt(252) : 0; // Annualized proxy

            if (sharpe < 0.5)
            {
                return Task.FromResult(RiskGuardResult.Block(GuardId, SignalReasonFlags.PortfolioOptimiserVeto,
                    $"Projected portfolio Sharpe ({sharpe:F2}) below threshold 0.5"));
            }
        }

        var currentHeat = openTrades.Sum(t => t.PositionSizePercent);
        if (currentHeat + decision.PositionSizePercent > 0.30m) // 30% total exposure limit
        {
            return Task.FromResult(RiskGuardResult.Block(GuardId, SignalReasonFlags.PortfolioOptimiserVeto,
                $"Total portfolio exposure ({currentHeat + decision.PositionSizePercent:P0}) exceeds 30% cap"));
        }

        return Task.FromResult(RiskGuardResult.Pass(GuardId));
    }
}
