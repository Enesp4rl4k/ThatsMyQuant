using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Application.Strategy;

public sealed class EdgeScoreService
{
    private readonly MarketExecutionProfileService _executionProfiles;

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

        var finalEdgeScore =
            (signalEdge * 0.30m) +
            (historicalEdge * 0.30m) +
            (executionEdge * 0.25m) +
            (riskContext * 0.15m);

        var adjustedEdgeScore = finalEdgeScore * (executionEdge / 100m);
        var reasons = new List<string>
        {
            $"SignalEdge={signalEdge:F1}",
            $"HistoricalEdge={historicalEdge:F1}",
            $"ExecutionEdge={executionEdge:F1}",
            $"RiskContext={riskContext:F1}"
        };

        if (executionEdge < 50m)
        {
            reasons.Add("Hard gate: ExecutionEdge < 50");
            return new EdgeScoreBreakdown(
                signalEdge,
                historicalEdge,
                executionEdge,
                riskContext,
                finalEdgeScore,
                adjustedEdgeScore,
                0m,
                "reject",
                false,
                reasons);
        }

        if (riskContext < 50m)
        {
            reasons.Add("Hard gate: RiskContext < 50");
            return new EdgeScoreBreakdown(
                signalEdge,
                historicalEdge,
                executionEdge,
                riskContext,
                finalEdgeScore,
                adjustedEdgeScore,
                0m,
                "reject",
                false,
                reasons);
        }

        if (adjustedEdgeScore >= 85m)
        {
            return new EdgeScoreBreakdown(
                signalEdge,
                historicalEdge,
                executionEdge,
                riskContext,
                finalEdgeScore,
                adjustedEdgeScore,
                1.00m,
                "full",
                true,
                reasons);
        }

        if (adjustedEdgeScore >= 70m)
        {
            reasons.Add("Band: reduced size");
            return new EdgeScoreBreakdown(
                signalEdge,
                historicalEdge,
                executionEdge,
                riskContext,
                finalEdgeScore,
                adjustedEdgeScore,
                0.65m,
                "reduced",
                true,
                reasons);
        }

        if (adjustedEdgeScore >= 55m)
        {
            reasons.Add("Band: probe/paper only");
            return new EdgeScoreBreakdown(
                signalEdge,
                historicalEdge,
                executionEdge,
                riskContext,
                finalEdgeScore,
                adjustedEdgeScore,
                0m,
                "probe",
                false,
                reasons);
        }

        reasons.Add("Band: reject");
        return new EdgeScoreBreakdown(
            signalEdge,
            historicalEdge,
            executionEdge,
            riskContext,
            finalEdgeScore,
            adjustedEdgeScore,
            0m,
            "reject",
            false,
            reasons);
    }

    private static decimal ComputeSignalEdge(TradeDecision decision)
    {
        decimal edge = decision.Confidence;

        if (decision.Indicators?.VolumeRatio is >= 1.5)
        {
            edge += 8m;
        }
        else if (decision.Indicators?.VolumeRatio is < 1.0)
        {
            edge -= 10m;
        }

        if (string.Equals(decision.RegimeTag, "Trending", StringComparison.OrdinalIgnoreCase))
        {
            edge += 4m;
        }
        else if (string.Equals(decision.RegimeTag, "Volatile", StringComparison.OrdinalIgnoreCase))
        {
            edge -= 3m;
        }

        return Math.Clamp(edge, 0m, 100m);
    }

    private static decimal ComputeHistoricalEdge(
        ExpectancyGuardResult expectancyGuard,
        SymbolGuardResult symbolGuard,
        PromotionGuardResult promotionGuard)
    {
        var expectancyScore = GuardMultiplierToScore(expectancyGuard.Approved, expectancyGuard.SizeMultiplier, 100m);
        var symbolScore = GuardMultiplierToScore(symbolGuard.Approved, symbolGuard.SizeMultiplier, 100m);
        var promotionScore = GuardMultiplierToScore(promotionGuard.Approved, promotionGuard.SizeMultiplier, 100m);
        var driftPenalty = (decimal)Math.Min(35d, promotionGuard.DriftScore * 35d);

        return Math.Clamp(
            (expectancyScore * 0.40m) +
            (symbolScore * 0.25m) +
            (promotionScore * 0.35m) -
            driftPenalty,
            0m,
            100m);
    }

    private static decimal ComputeExecutionEdge(
        Signal signal,
        TradeDecision decision,
        PromotionGuardResult promotionGuard,
        MarketExecutionProfile profile)
    {
        decimal edge = 78m;

        if (signal.Volume24h > 0)
        {
            var liquidityRatio = signal.Volume24h / Math.Max(1m, profile.MinHftVolume24h);
            if (liquidityRatio >= 2m) edge += 12m;
            else if (liquidityRatio >= 1m) edge += 4m;
            else if (liquidityRatio < 0.75m) edge -= 25m;
            else edge -= 12m;
        }

        if (string.Equals(decision.StrategyTag, "HftMicroEdge", StringComparison.OrdinalIgnoreCase))
        {
            if (promotionGuard.Reason.Contains("latency", StringComparison.OrdinalIgnoreCase))
            {
                edge -= promotionGuard.Approved ? 22m : 40m;
            }

            if (promotionGuard.Reason.Contains("slip", StringComparison.OrdinalIgnoreCase))
            {
                edge -= 12m;
            }
        }

        if (promotionGuard.DriftScore >= 0.75)
        {
            edge -= 25m;
        }
        else if (promotionGuard.DriftScore >= 0.40)
        {
            edge -= 12m;
        }

        return Math.Clamp(edge, 0m, 100m);
    }

    private static decimal ComputeRiskContext(
        Signal signal,
        TradeDecision decision,
        (bool Approved, string Reason) portfolioGuard,
        (bool Approved, string Reason, decimal SafePositionSizePercent) riskVote)
    {
        decimal score = 80m;

        if (!portfolioGuard.Approved)
        {
            score -= 45m;
        }

        if (!riskVote.Approved)
        {
            score -= 35m;
        }

        var sessionTag = ResolveSessionBucket(signal.Timestamp);
        score += sessionTag switch
        {
            "London Killzone" => 10m,
            "NY Killzone" => 8m,
            "Asia" => -5m,
            _ => -20m
        };

        if (string.Equals(decision.RegimeTag, "Volatile", StringComparison.OrdinalIgnoreCase))
        {
            score -= 8m;
        }
        else if (string.Equals(decision.RegimeTag, "Trending", StringComparison.OrdinalIgnoreCase))
        {
            score += 5m;
        }

        return Math.Clamp(score, 0m, 100m);
    }

    private static decimal GuardMultiplierToScore(bool approved, decimal multiplier, decimal maxScore)
    {
        if (!approved)
        {
            return 5m;
        }

        return Math.Clamp(maxScore * multiplier, 0m, maxScore);
    }

    private static string ResolveSessionBucket(DateTime timestamp)
    {
        var turkeyTime = timestamp.Kind == DateTimeKind.Utc
            ? TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp, "Turkey Standard Time")
            : TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp.ToUniversalTime(), "Turkey Standard Time");

        return turkeyTime.Hour switch
        {
            >= 10 and < 13 => "London Killzone",
            >= 15 and < 18 => "NY Killzone",
            >= 3 and < 10 => "Asia",
            _ => "Close / Dead Zone"
        };
    }
}

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
