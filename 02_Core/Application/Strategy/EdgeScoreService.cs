using ThatsMyQuant.Contracts.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ThatsMyQuant.Application.Strategy;

public sealed class EdgeScoreService
{
    private readonly MarketExecutionProfileService _executionProfiles;
    private readonly RegimeExpectancyService? _regimeExpectancy;

    // Raw component weights for the "final" (pre-execution-emphasis) score. Sum = 1.00.
    // Kept for backwards-compat reporting in EdgeScoreBreakdown.FinalEdgeScore.
    private const decimal WeightSignal = 0.30m;
    private const decimal WeightHistorical = 0.30m;
    private const decimal WeightExecution = 0.25m;
    private const decimal WeightRisk = 0.15m;

    // The adjusted score applies a second-pass emphasis on execution (liquidity + cost),
    // which in live trading dominates marginal-edge outcomes. Algebraically:
    //     adjusted = final * 0.70 + execution * 0.30
    //              = signal*0.21 + historical*0.21 + execution*(0.25*0.70+0.30) + risk*0.105
    //              = signal*0.21 + historical*0.21 + execution*0.475 + risk*0.105    (Σ=1.00)
    // Below are the effective weights — documented explicitly so future tuning doesn't
    // fight the double-blend and is instead changed in one place.
    private const decimal AdjWeightSignal = 0.21m;
    private const decimal AdjWeightHistorical = 0.21m;
    private const decimal AdjWeightExecution = 0.475m;
    private const decimal AdjWeightRisk = 0.105m;

    public EdgeScoreService(MarketExecutionProfileService? executionProfiles = null, RegimeExpectancyService? regimeExpectancy = null)
    {
        _executionProfiles = executionProfiles ?? new MarketExecutionProfileService();
        _regimeExpectancy = regimeExpectancy;
    }

    public EdgeScoreBreakdown Evaluate(
        Signal signal,
        TradeDecision decision,
        EvaluationGuards guards)
        => Evaluate(signal, decision, guards, null);

    public EdgeScoreBreakdown Evaluate(
        Signal signal,
        TradeDecision decision,
        EvaluationGuards guards,
        EdgeScoreEvaluationOptions? options)
    {
        var profile = _executionProfiles.ResolveForSymbol(signal.Symbol, signal.Metadata);
        var reasons = new List<string>();
        var signalEdge = ComputeSignalEdge(decision, _regimeExpectancy);
        var regressionGate = ApplyLinearRegressionOverlay(decision, ref signalEdge, reasons);

        var metrics = new ScoreMetrics(
            signalEdge,
            ComputeHistoricalEdge(guards.Expectancy, guards.Symbol, guards.Promotion, decision.RegimeTag, _regimeExpectancy),
            ComputeExecutionEdge(signal, decision, profile, reasons),
            ComputeRiskContext(decision.PositionSizePercent, guards.Portfolio, guards.Risk)
        );

        var finalScore = (metrics.Signal * WeightSignal) + (metrics.Historical * WeightHistorical) +
                         (metrics.Execution * WeightExecution) + (metrics.Risk * WeightRisk);

        // Execution-emphasis blend — pre-computed effective weights (sum = 1.00).
        var adjustedScore = (metrics.Signal * AdjWeightSignal) +
                            (metrics.Historical * AdjWeightHistorical) +
                            (metrics.Execution * AdjWeightExecution) +
                            (metrics.Risk * AdjWeightRisk);

        reasons.AddRange(new[]
        {
            $"S:{metrics.Signal:F1}", $"H:{metrics.Historical:F1}", $"E:{metrics.Execution:F1}", $"R:{metrics.Risk:F1}"
        });

        return ResolveFinalDecision(metrics, finalScore, adjustedScore, reasons, options, regressionGate);
    }

    private EdgeScoreBreakdown ResolveFinalDecision(
        ScoreMetrics m,
        decimal final,
        decimal adj,
        List<string> reasons,
        EdgeScoreEvaluationOptions? options,
        RegressionGateResult regressionGate)
    {
        var kind = options?.PipelineKind ?? EdgePipelineKind.Live;

        if (regressionGate.BlockReason is not null)
            return CreateBreakdown(m, new BreakdownResult(final, adj, 0m, "reject", false), reasons, regressionGate.BlockReason);

        // Liquidity hard gate applies only when real capital is at stake.
        if (kind == EdgePipelineKind.Live && m.Execution < 60m)
            return CreateBreakdown(m, new BreakdownResult(final, adj, 0m, "reject", false), reasons, "Hard gate: Low Execution (<60)");
        if (kind != EdgePipelineKind.Live && m.Execution < 60m)
            reasons.Add("Research pipeline: liquidity gate not applied to live capital");

        if (m.Risk < 50m) return CreateBreakdown(m, new BreakdownResult(final, adj, 0m, "reject", false), reasons, "Hard gate: High Risk");

        // 2026-04-24 alpha-widening pass: lowered bands to let marginal-but-positive edge through.
        // Previously 85/70/62 was starving backtest of signals; audit showed many rejects at 58-62
        // had positive historical expectancy. Probe band still size-capped so tail risk stays bounded.
        return adj switch
        {
            >= 82m => CreateBreakdown(m, new BreakdownResult(final, adj, 1.00m, "full", true), reasons),
            >= 66m => CreateBreakdown(m, new BreakdownResult(final, adj, 0.65m, "reduced", true), reasons, "Band: reduced size"),
            >= 58m => ResolveProbeBand(m, final, adj, reasons, options),
            _ => CreateBreakdown(m, new BreakdownResult(final, adj, 0m, "reject", false), reasons, "Band: reject")
        };
    }

    private EdgeScoreBreakdown ResolveProbeBand(ScoreMetrics m, decimal final, decimal adj, List<string> reasons, EdgeScoreEvaluationOptions? options)
    {
        var kind = options?.PipelineKind ?? EdgePipelineKind.Live;
        if (kind == EdgePipelineKind.Live)
            return CreateBreakdown(m, new BreakdownResult(final, adj, 0m, "probe", false), reasons, "Band: probe/paper only");

        var mult = options?.SimulatedProbeSizeMultiplier ?? 0.35m;
        return CreateBreakdown(m, new BreakdownResult(final, adj, mult, "probe", true), reasons,
            $"Research pipeline ({kind}): probe band allowed at {mult:P0} size");
    }

    private static EdgeScoreBreakdown CreateBreakdown(ScoreMetrics m, BreakdownResult res, List<string> reasons, string? extraReason = null)
    {
        if (extraReason != null) reasons.Add(extraReason);
        return new EdgeScoreBreakdown(m.Signal, m.Historical, m.Execution, m.Risk, res.Final, res.Adjusted, res.Multiplier, res.Band, res.Approved, reasons);
    }

    private readonly record struct BreakdownResult(decimal Final, decimal Adjusted, decimal Multiplier, string Band, bool Approved);
    private readonly record struct RegressionGateResult(string? BlockReason);

    private static decimal ComputeSignalEdge(TradeDecision decision, RegimeExpectancyService? regimeSvc)
    {
        decimal confidenceEdge = decision.Confidence;
        confidenceEdge += decision.RegimeTag?.ToLower() switch { "trending" => 4m, "volatile" => -3m, _ => 0m };
        confidenceEdge = Math.Clamp(confidenceEdge, 0m, 100m);

        // Blend with realized OOS alpha when regime data exists.
        // This prevents a losing strategy at 80% confidence from scoring 84 on signal edge.
        if (regimeSvc is not null && !string.IsNullOrWhiteSpace(decision.RegimeTag))
        {
            var re = regimeSvc.GetExpectancy(decision.RegimeTag);
            if (re.TradeCount >= 10)
            {
                // OOS alpha score: Wilson lower bound win rate × 100, adjusted by expectancy sign.
                var oosWrScore = (decimal)Math.Clamp(re.WinRateWilsonLower * 100, 0, 100);
                var oosExpSign = re.ExpectancyPerTrade > 0m ? 1.0m : re.ExpectancyPerTrade == 0m ? 0.85m : 0.6m;
                var oosAlpha = oosWrScore * oosExpSign;

                // Weight: 65% confidence, 35% realized OOS alpha (more data → more trust in OOS).
                var oosWeight = re.IsTrustworthy ? 0.35m : 0.20m;
                return Math.Clamp(
                    (confidenceEdge * (1m - oosWeight)) + (oosAlpha * oosWeight),
                    0m, 100m);
            }
        }

        return confidenceEdge;
    }

    private static decimal ComputeHistoricalEdge(ExpectancyGuardResult exp, SymbolGuardResult sym, PromotionGuardResult prom, string? regimeTag, RegimeExpectancyService? regimeSvc)
    {
        var expectancyScore = exp.Approved ? Math.Clamp(100m * exp.SizeMultiplier, 0m, 100m) : 5m;
        var symbolScore = sym.Approved ? Math.Clamp(100m * sym.SizeMultiplier, 0m, 100m) : 5m;
        var promotionScore = prom.Approved ? Math.Clamp(100m * prom.SizeMultiplier, 0m, 100m) : 5m;
        var driftPenalty = (decimal)Math.Min(35d, prom.DriftScore * 35d);

        // OOS regime expectancy component: downweight when live regime has thin or losing OOS
        // track record; boost when OOS WR is trustworthy (n>=30, Wilson-lower >= 45%).
        decimal regimeComponent = 50m;
        if (regimeSvc is not null && !string.IsNullOrWhiteSpace(regimeTag))
        {
            var re = regimeSvc.GetExpectancy(regimeTag);
            if (re.TradeCount >= 10)
            {
                // Scale 0..100 from Wilson lower bound and profit factor.
                var wrScore = (decimal)Math.Clamp(re.WinRateWilsonLower * 100, 0, 100);
                var pfScore = re.ProfitFactor switch
                {
                    >= 1.5m => 100m,
                    >= 1.2m => 75m,
                    >= 1.0m => 55m,
                    >= 0.9m => 35m,
                    _ => 10m
                };
                regimeComponent = (wrScore * 0.6m) + (pfScore * 0.4m);
                if (!re.IsTrustworthy) regimeComponent *= 0.7m; // shrink toward untrusted band
            }
        }

        return Math.Clamp(
            (expectancyScore * 0.30m) + (symbolScore * 0.20m) + (promotionScore * 0.25m) + (regimeComponent * 0.25m) - driftPenalty,
            0m, 100m);
    }

    private static decimal ComputeExecutionEdge(
        Signal signal,
        TradeDecision decision,
        MarketExecutionProfile profile,
        List<string> reasons)
    {
        // Unknown 24h volume should not read as "illiquid" — treat as neutral so webhooks/backtests without venue volume still score.
        decimal edge = 50m;

        if (signal.Volume24h > 0)
        {
            var ratio = signal.Volume24h / Math.Max(1m, profile.MinHftVolume24h);
            edge += ratio switch { >= 2m => 12m, >= 1m => 4m, < 0.75m => -25m, _ => -12m };
        }

        var rewardPercent = signal.Price > 0m && decision.TakeProfit > 0m
            ? Math.Abs(decision.TakeProfit - signal.Price) / signal.Price * 100m
            : 0m;
        var roundTripCostPercent = profile.SpreadPercent + (profile.SlippagePercent * 2m) + (profile.CommissionPercentPerSide * 2m);
        if (rewardPercent > 0m && roundTripCostPercent > 0m)
        {
            var rewardToCost = rewardPercent / roundTripCostPercent;
            // Monotonic reward-to-cost curve: previously had a gap where 4.0x paid -8 but 5.0x paid +3
            // (a 1.0x improvement flipped the score by +11). Replaced with piecewise-linear bands.
            edge += rewardToCost switch
            {
                >= 8m => 10m,
                >= 6m => 6m,
                >= 4.5m => 3m,
                >= 3.5m => 0m,
                >= 2.5m => -6m,
                _ => -18m
            };

            if (rewardToCost < 3.5m)
                reasons.Add($"Execution cost caution: reward/cost {rewardToCost:F1}x.");
        }

        return Math.Clamp(edge, 0m, 100m);
    }

    private static decimal ComputeRiskContext(
        decimal requestedPositionSize,
        (bool Approved, string Reason) portfolioGuard,
        (bool Approved, string Reason, decimal SafeSize) riskVote)
    {
        if (!portfolioGuard.Approved) return 0m;
        if (!riskVote.Approved) return 0m;
        if (riskVote.SafeSize <= 0m) return 0m;

        var requested = requestedPositionSize > 0m ? requestedPositionSize : riskVote.SafeSize;
        var safeSizeRatio = requested > 0m
            ? Math.Clamp(riskVote.SafeSize / requested, 0m, 1m)
            : 1m;
        return Math.Clamp(55m + (45m * safeSizeRatio), 0m, 100m);
    }

    private static RegressionGateResult ApplyLinearRegressionOverlay(
        TradeDecision decision,
        ref decimal signalEdge,
        List<string> reasons)
    {
        var regression = decision.Indicators?.LinearRegression;
        if (regression is null)
            return default;

        reasons.Add(
            $"LR:{regression.NormalizedSlope:+0.000;-0.000;0.000}ATR R2:{regression.RSquared:F2} d:{regression.SigmaDistance:+0.0;-0.0;0.0}sigma");

        if (decision.Side == ThatsMyQuant.Contracts.Enums.TradeSide.Buy && regression.NormalizedSlope < 0m)
        {
            signalEdge = Math.Clamp(signalEdge * 0.80m, 0m, 100m);
            reasons.Add("Linear regression penalty: long setup against negative slope (-20% confidence).");
        }

        if (regression.IsOverextendedAbove && IsTrendFollowingLong(decision))
        {
            reasons.Add("Linear regression flagged price as overextended above +2.0sigma.");
            return new RegressionGateResult("Hard gate: Overextended (>2.0sigma) blocks trend-following longs");
        }

        return default;
    }

    private static bool IsTrendFollowingLong(TradeDecision decision)
    {
        if (decision.Side != ThatsMyQuant.Contracts.Enums.TradeSide.Buy)
            return false;

        if (string.Equals(decision.StrategyTag, "RangeReversion", StringComparison.OrdinalIgnoreCase))
            return false;

        return decision.StrategyTag.Contains("Trend", StringComparison.OrdinalIgnoreCase) ||
               decision.StrategyTag.Contains("Breakout", StringComparison.OrdinalIgnoreCase) ||
               decision.StrategyTag.Contains("Momentum", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(decision.ExitPolicyTag, "TrendRunner", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(decision.ExitPolicyTag, "BreakoutExpansion", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(decision.ExitPolicyTag, "HftScalp", StringComparison.OrdinalIgnoreCase);
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
