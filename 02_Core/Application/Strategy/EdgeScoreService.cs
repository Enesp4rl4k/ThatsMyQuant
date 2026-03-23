using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.ValueObjects;
using System;
using System.Collections.Generic;

namespace ThatsMyQuant.Application.Strategy;

public sealed class EdgeScoreService
{
    private readonly MarketExecutionProfileService _executionProfiles;

    private const decimal WeightSignal = 0.30m;
    private const decimal WeightHistorical = 0.30m;
    private const decimal WeightExecution = 0.25m;
    private const decimal WeightRisk = 0.15m;

    public EdgeScoreService(MarketExecutionProfileService? executionProfiles = null)
    {
        _executionProfiles = executionProfiles ?? new MarketExecutionProfileService();
    }

    public EdgeScoreBreakdown Evaluate(
        Signal signal,
        TradeDecision decision,
        ExpectancyGuardResult expectancyGuard,
        SymbolGuardResult symbolGuard,
        PromotionGuardResult promotionGuard,
        (bool Approved, string Reason) portfolioGuard,
        (bool Approved, string Reason, decimal SafePositionSizePercent) riskVote)
    {
        var profile = _executionProfiles.ResolveForSymbol(signal.Symbol, signal.Metadata);

        var signalEdge = ComputeSignalEdge(decision);
        var historicalEdge = ComputeHistoricalEdge(expectancyGuard, symbolGuard, promotionGuard);
        var executionEdge = ComputeExecutionEdge(signal, decision, promotionGuard, profile);
        var riskContext = ComputeRiskContext(signal, decision, portfolioGuard, riskVote);

        var finalScore = (signalEdge * WeightSignal) + (historicalEdge * WeightHistorical) +
                         (executionEdge * WeightExecution) + (riskContext * WeightRisk);

        var adjustedScore = finalScore * (executionEdge / 100m);

        var reasons = new List<string> {
            $"S:{signalEdge:F1}", $"H:{historicalEdge:F1}", $"E:{executionEdge:F1}", $"R:{riskContext:F1}"
        };

        return ResolveFinalDecision(signalEdge, historicalEdge, executionEdge, riskContext, finalScore, adjustedScore, reasons);
    }

    private EdgeScoreBreakdown ResolveFinalDecision(decimal s, decimal h, decimal e, decimal r, decimal final, decimal adj, List<string> reasons)
    {
        if (e < 50m) return CreateBreakdown(s, h, e, r, final, adj, 0m, "reject", false, reasons, "Hard gate: Low Execution");
        if (r < 50m) return CreateBreakdown(s, h, e, r, final, adj, 0m, "reject", false, reasons, "Hard gate: High Risk");

        return adj switch
        {
            >= 85m => CreateBreakdown(s, h, e, r, final, adj, 1.00m, "full", true, reasons),
            >= 70m => CreateBreakdown(s, h, e, r, final, adj, 0.65m, "reduced", true, reasons, "Band: reduced size"),
            >= 55m => CreateBreakdown(s, h, e, r, final, adj, 0m, "probe", false, reasons, "Band: probe/paper only"),
            _ => CreateBreakdown(s, h, e, r, final, adj, 0m, "reject", false, reasons, "Band: reject")
        };
    }

    private static EdgeScoreBreakdown CreateBreakdown(decimal s, decimal h, decimal e, decimal r, decimal f, decimal a, decimal mult, string band, bool approved, List<string> reasons, string? extraReason = null)
    {
        if (extraReason != null) reasons.Add(extraReason);
        return new EdgeScoreBreakdown(s, h, e, r, f, a, mult, band, approved, reasons);
    }

    private static decimal ComputeSignalEdge(TradeDecision decision)
    {
        decimal edge = decision.Confidence;
        edge += decision.RegimeTag?.ToLower() switch { "trending" => 4m, "volatile" => -3m, _ => 0m };
        return Math.Clamp(edge, 0m, 100m);
    }

    private static decimal ComputeHistoricalEdge(ExpectancyGuardResult exp, SymbolGuardResult sym, PromotionGuardResult prom)
    {
        var expectancyScore = exp.Approved ? Math.Clamp(100m * exp.SizeMultiplier, 0m, 100m) : 5m;
        var symbolScore = sym.Approved ? Math.Clamp(100m * sym.SizeMultiplier, 0m, 100m) : 5m;
        var promotionScore = prom.Approved ? Math.Clamp(100m * prom.SizeMultiplier, 0m, 100m) : 5m;
        var driftPenalty = (decimal)Math.Min(35d, prom.DriftScore * 35d);

        return Math.Clamp((expectancyScore * 0.40m) + (symbolScore * 0.25m) + (promotionScore * 0.35m) - driftPenalty, 0m, 100m);
    }

    private static decimal ComputeExecutionEdge(Signal signal, TradeDecision decision, PromotionGuardResult promotionGuard, MarketExecutionProfile profile)
    {
        decimal edge = 78m;
        if (signal.Volume24h > 0)
        {
            var ratio = signal.Volume24h / Math.Max(1m, profile.MinHftVolume24h);
            edge += ratio switch { >= 2m => 12m, >= 1m => 4m, < 0.75m => -25m, _ => -12m };
        }
        return Math.Clamp(edge, 0m, 100m);
    }

    private static decimal ComputeRiskContext(Signal signal, TradeDecision decision, (bool Approved, string Reason) portfolioGuard, (bool Approved, string Reason, decimal SafeSize) riskVote)
    {
        decimal score = 80m;
        if (!portfolioGuard.Approved) score -= 45m;
        if (!riskVote.Approved) score -= 35m;
        return Math.Clamp(score, 0m, 100m);
    }
}

// --- SONARQUBE FIX: MISSING TYPE ADDED HERE ---
public sealed record EdgeScoreBreakdown(
    decimal SignalEdge,
    decimal HistoricalEdge,
    decimal ExecutionEdge,
    decimal RiskContext,
    decimal FinalEdgeScore,
    decimal AdjustedEdgeScore,
    decimal RecommendedSizeMultiplier,
    string DecisionBand,
    bool Approved,
    IReadOnlyList<string> Reasons);