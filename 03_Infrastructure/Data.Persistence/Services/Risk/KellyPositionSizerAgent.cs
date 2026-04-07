using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ThatsMyQuant.Infrastructure.Services.Risk;

public sealed class KellyPositionSizerAgent : IRiskGuardAgent
{
    private readonly ILogger<KellyPositionSizerAgent> _logger;
    private readonly double _fractionalKelly;
    private readonly double _minEdge;
    private readonly double _maxPositionFraction;
    private readonly double _totalTransactionCostBps;

    public string GuardId => "Risk.Kelly";
    public int Order => 0;

    public KellyPositionSizerAgent(ILogger<KellyPositionSizerAgent> logger, IOptions<RiskGuardAgentOptions>? options = null)
    {
        _logger = logger;
        var opts = options?.Value ?? new RiskGuardAgentOptions();
        _fractionalKelly = opts.FractionalKelly;
        _minEdge = opts.MinEdge;
        _maxPositionFraction = opts.MaxPositionFraction;
        _totalTransactionCostBps = opts.TotalTransactionCostBps;
    }

    public Task<RiskGuardResult> EvaluateAsync(
        Signal signal,
        TradeDecision decision,
        IReadOnlyList<Trade> openTrades,
        CancellationToken ct = default)
    {
        var p = (double)decision.Confidence / 100.0;
        var q = 1.0 - p;
        
        // Odds ratio (b): Net Profit / Net Loss
        var rawTakeProfitDist = Math.Abs((double)(decision.TakeProfit - signal.Price));
        var rawStopLossDist = Math.Abs((double)(decision.StopLoss - signal.Price));
        var estimatedCost = (double)signal.Price * _totalTransactionCostBps;

        var takeProfitDist = rawTakeProfitDist - estimatedCost;
        var stopLossDist = rawStopLossDist + estimatedCost;

        if (takeProfitDist <= 0)
        {
            return Task.FromResult(RiskGuardResult.Block(GuardId, SignalReasonFlags.KellyVeto,
                $"Negative edge after transaction costs (Cost={estimatedCost:F4})"));
        }

        var b = stopLossDist > 0 ? takeProfitDist / stopLossDist : 1.5;

        // Full Kelly = (bp - q) / b
        var fullKelly = (b * p - q) / b;
        
        if (fullKelly <= 0)
        {
            return Task.FromResult(RiskGuardResult.Block(GuardId, SignalReasonFlags.KellyVeto,
                $"Negative expectancy (Kelly={fullKelly:F2})"));
        }

        // Fractional Kelly capped at max position fraction
        var fractionalKellyResult = fullKelly * _fractionalKelly;
        var finalFraction = (decimal)Math.Min(fractionalKellyResult, _maxPositionFraction);

        // SizeMultiplier is relative to the initial decision size
        var multiplier = finalFraction / decision.PositionSizePercent;

        return Task.FromResult(RiskGuardResult.Pass(GuardId, Math.Clamp(multiplier, 0.0m, 1.0m)));
    }
}
