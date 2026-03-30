using ThatsMyQuant.Contracts.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ThatsMyQuant.Application.Strategy;

public sealed class EdgeScoreService
{
    private readonly MarketExecutionProfileService _executionProfiles;

    private const decimal WeightSignal = 0.30m;
    private const decimal WeightHistorical = 0.30m;
    private const decimal WeightExecution = 0.25m;
    private const decimal WeightRisk = 0.15m;
    private const decimal CoreWeight = 0.70m;
    private const decimal ExecutionBlendWeight = 0.30m;

    public EdgeScoreService(MarketExecutionProfileService? executionProfiles = null)
    {
        _executionProfiles = executionProfiles ?? new MarketExecutionProfileService();
    }

    public EdgeScoreBreakdown Evaluate(
        Signal signal,
        TradeDecision decision,
        EvaluationGuards guards)
    {
        var profile = _executionProfiles.ResolveForSymbol(signal.Symbol, signal.Metadata);

        var metrics = new ScoreMetrics(
            ComputeSignalEdge(decision),
            ComputeHistoricalEdge(guards.Expectancy, guards.Symbol, guards.Promotion),
            ComputeExecutionEdge(signal, profile),
            ComputeRiskContext(guards.Portfolio, guards.Risk)
        );

        var finalScore = (metrics.Signal * WeightSignal) + (metrics.Historical * WeightHistorical) +
                         (metrics.Execution * WeightExecution) + (metrics.Risk * WeightRisk);

        var adjustedScore = (finalScore * CoreWeight) + (metrics.Execution * ExecutionBlendWeight);

        var reasons = new List<string> {
            $"S:{metrics.Signal:F1}", $"H:{metrics.Historical:F1}", $"E:{metrics.Execution:F1}", $"R:{metrics.Risk:F1}"
        };

        return ResolveFinalDecision(metrics, finalScore, adjustedScore, reasons);
    }

    private EdgeScoreBreakdown ResolveFinalDecision(ScoreMetrics m, decimal final, decimal adj, List<string> reasons)
    {
        if (m.Execution < 60m) return CreateBreakdown(m, new BreakdownResult(final, adj, 0m, "reject", false), reasons, "Hard gate: Low Execution (<60)");
        if (m.Risk < 50m) return CreateBreakdown(m, new BreakdownResult(final, adj, 0m, "reject", false), reasons, "Hard gate: High Risk");

        return adj switch
        {
            >= 85m => CreateBreakdown(m, new BreakdownResult(final, adj, 1.00m, "full", true), reasons),
            >= 70m => CreateBreakdown(m, new BreakdownResult(final, adj, 0.65m, "reduced", true), reasons, "Band: reduced size"),
            >= 55m => CreateBreakdown(m, new BreakdownResult(final, adj, 0m, "probe", false), reasons, "Band: probe/paper only"),
            _ => CreateBreakdown(m, new BreakdownResult(final, adj, 0m, "reject", false), reasons, "Band: reject")
        };
    }

    private static EdgeScoreBreakdown CreateBreakdown(ScoreMetrics m, BreakdownResult res, List<string> reasons, string? extraReason = null)
    {
        if (extraReason != null) reasons.Add(extraReason);
        return new EdgeScoreBreakdown(m.Signal, m.Historical, m.Execution, m.Risk, res.Final, res.Adjusted, res.Multiplier, res.Band, res.Approved, reasons);
    }

    private readonly record struct BreakdownResult(decimal Final, decimal Adjusted, decimal Multiplier, string Band, bool Approved);

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

    private static decimal ComputeExecutionEdge(Signal signal, MarketExecutionProfile profile)
    {
        decimal edge = 50m;
        if (signal.Volume24h > 0)
        {
            var ratio = signal.Volume24h / Math.Max(1m, profile.MinHftVolume24h);
            edge += ratio switch { >= 2m => 12m, >= 1m => 4m, < 0.75m => -25m, _ => -12m };
        }
        return Math.Clamp(edge, 0m, 100m);
    }

    private static decimal ComputeRiskContext((bool Approved, string Reason) portfolioGuard, (bool Approved, string Reason, decimal SafeSize) riskVote)
    {
        if (!portfolioGuard.Approved) return 0m;
        if (!riskVote.Approved) return 0m;
        return 80m;
    }
}

// --- EVALUATION CONTEXT STRUCTURES ---
public sealed record EvaluationGuards(
    ExpectancyGuardResult Expectancy,
    SymbolGuardResult Symbol,
    PromotionGuardResult Promotion,
    (bool Approved, string Reason) Portfolio,
    (bool Approved, string Reason, decimal SafePositionSizePercent) Risk);

internal readonly record struct ScoreMetrics(decimal Signal, decimal Historical, decimal Execution, decimal Risk);

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
    IReadOnlyCollection<string> Reasons);
