using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.Risk;

public sealed class KellyPositionSizerAgent : IRiskGuardAgent
{
    private readonly ILogger<KellyPositionSizerAgent> _logger;
    private const double FractionalKelly = 0.25;
    private const double MinEdge = 0.02;
    private const double MaxPositionFraction = 0.20;

    public string GuardId => "Risk.Kelly";
    public int Order => 0;

    public KellyPositionSizerAgent(ILogger<KellyPositionSizerAgent> logger)
        => _logger = logger;

    public Task<RiskGuardResult> EvaluateAsync(
        Signal signal,
        TradeDecision decision,
        IReadOnlyList<Trade> openTrades,
        CancellationToken ct = default)
    {
        var p = (double)decision.Confidence / 100.0;
        var q = 1.0 - p;
        
        // Odds ratio (b): Profit / Loss
        var takeProfitDist = Math.Abs((double)(decision.TakeProfit - signal.Price));
        var stopLossDist = Math.Abs((double)(decision.StopLoss - signal.Price));
        var b = stopLossDist > 0 ? takeProfitDist / stopLossDist : 1.5;

        // Full Kelly = (bp - q) / b
        var fullKelly = (b * p - q) / b;
        
        if (fullKelly <= 0)
        {
            return Task.FromResult(RiskGuardResult.Block(GuardId, SignalReasonFlags.KellyVeto,
                $"Negative expectancy (Kelly={fullKelly:F2})"));
        }

        // 0.5x fractional Kelly capped at 20% position
        var halfKelly = fullKelly * 0.5;
        var finalFraction = (decimal)Math.Min(halfKelly, 0.20);

        // SizeMultiplier is relative to the initial decision size
        var multiplier = finalFraction / decision.PositionSizePercent;

        return Task.FromResult(RiskGuardResult.Pass(GuardId, Math.Clamp(multiplier, 0.0m, 1.0m)));
    }
}
