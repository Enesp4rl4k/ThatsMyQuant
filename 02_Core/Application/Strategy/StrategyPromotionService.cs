using Microsoft.Extensions.Configuration;
using ThatsMyQuant.Contracts.Options;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using System.Threading;
using static ThatsMyQuant.Application.Strategy.StrategyMathUtils;

namespace ThatsMyQuant.Application.Strategy;

public sealed class StrategyPromotionService
{
    private readonly IStrategyPerformanceStore _store;
    private readonly MarketExecutionProfileService _executionProfiles;
    private readonly object _sync = new();
    private StrategyPerformanceState _state;
    private readonly BacktestQualityGateOptions _qualityGate;
    private readonly StrategyPromotionOptions _promotionOptions;

    // Promotion criteria — configurable via appsettings.json or env vars
    private readonly int _backtestMinTrades;
    private readonly double _backtestMinProfitFactor;
    private readonly double _backtestMinWinRate;
    private readonly int _paperMinTrades;
    private readonly double _paperMinProfitFactor;
    private readonly double _paperMinWinRate;

    public StrategyPromotionService(
        IStrategyPerformanceStore store,
        MarketExecutionProfileService? executionProfiles = null,
        IConfiguration? configuration = null,
        StrategyPromotionOptions? promotionOptions = null)
    {
        _store = store;
        _executionProfiles = executionProfiles ?? new MarketExecutionProfileService();
        _state = new StrategyPerformanceState();
        _qualityGate = BuildQualityGate(configuration);
        _promotionOptions = promotionOptions ?? BuildPromotionOptions(configuration);

        _backtestMinTrades      = int.TryParse(configuration?["Promotion:Backtest:MinTrades"],       out var btTrades)      ? btTrades      : _qualityGate.MinTrades;
        _backtestMinProfitFactor= double.TryParse(configuration?["Promotion:Backtest:MinProfitFactor"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var btPf)       ? btPf          : _qualityGate.MinProfitFactor;
        _backtestMinWinRate     = double.TryParse(configuration?["Promotion:Backtest:MinWinRate"],    System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var btWr)          ? btWr          : _qualityGate.MinWinRatePercent / 100.0;
        _paperMinTrades         = int.TryParse(configuration?["Promotion:Paper:MinTrades"],           out var ptTrades)      ? ptTrades      : 30;
        _paperMinProfitFactor   = double.TryParse(configuration?["Promotion:Paper:MinProfitFactor"],  System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ptPf)          ? ptPf          : 1.30;
        _paperMinWinRate        = double.TryParse(configuration?["Promotion:Paper:MinWinRate"],       System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ptWr)          ? ptWr          : 0.50;
    }

    public async Task InitializeAsync()
    {
        _state = await _store.LoadAsync();
    }

    public PromotionGuardResult Evaluate(Signal signal, TradeDecision decision, IReadOnlyList<Trade> liveTrades)
    {
        lock (_sync)
        {
            RefreshLiveState(liveTrades);

            var sessionTag = ResolveSessionBucket(signal.Timestamp);
            var backtestMatch = FindBestBucketMatch(_state.BacktestBuckets, signal.Symbol, decision.StrategyTag, decision.RegimeTag, sessionTag);
            var paperMatch = FindBestBucketMatch(_state.PaperBuckets, signal.Symbol, decision.StrategyTag, decision.RegimeTag, sessionTag);
            var liveMatch = FindBestBucketMatch(_state.LiveBuckets, signal.Symbol, decision.StrategyTag, decision.RegimeTag, sessionTag);
            
            var capitalBiasMultiplier = ComputeCapitalBiasMultiplier(backtestMatch, paperMatch, liveMatch);
            var capitalBiasLabel = BuildCapitalBiasLabel(backtestMatch, paperMatch, liveMatch, capitalBiasMultiplier);
            
            var mlDriftRes = CheckMlDrift(liveTrades, capitalBiasMultiplier, capitalBiasLabel);
            if (mlDriftRes != null) return mlDriftRes.Value;

            var res = PerformPromotionGuardChecks(signal, decision, backtestMatch, paperMatch, liveMatch, capitalBiasMultiplier, capitalBiasLabel);
            if (res != null) return res.Value;

            var driftScoreValue = (paperMatch.Bucket != null && liveMatch.Bucket != null) ? ComputeDriftScore(paperMatch.Bucket, liveMatch.Bucket) : 0;
            if (paperMatch.Bucket != null && liveMatch.Bucket != null)
            {
                res = CheckPaperLiveDrift(sessionTag, liveMatch.Bucket, driftScoreValue, capitalBiasMultiplier, capitalBiasLabel);
                if (res != null) return res.Value;
            }

            return CreateFinalPromotionResult(liveMatch.Bucket ?? new StrategyBucketPerformanceSnapshot(), null, capitalBiasMultiplier, capitalBiasLabel, driftScoreValue);
        }
    }

    private PromotionGuardResult? PerformPromotionGuardChecks(
        Signal signal, TradeDecision decision, 
        BucketMatch backtest, BucketMatch paper, BucketMatch live, 
        decimal capitalBias, string biasLabel)
    {
        var res = CheckLadderGates(signal, decision, backtest.Bucket, paper.Bucket);
        if (res != null) return res;

        res = CheckLiveSampleDepth(live.Bucket, capitalBias, biasLabel);
        if (res != null) return res;

        var exactBucketSizingBias = ComputeExactBucketSizingBias(signal, decision, live);
        var venueExecutionBias = ComputeVenueExecutionBias(signal, live);
        var concordancePromotionBias = ComputeExactVenueConcordancePromotionBias(signal, decision, exactBucketSizingBias, venueExecutionBias);
        
        res = CheckConcordanceBias(concordancePromotionBias, capitalBias, biasLabel);
        if (res != null) return res;

        var venuePromotionBias = ComputeVenuePromotionBias(signal, live);
        res = CheckVenuePromotionBias(venuePromotionBias, capitalBias, biasLabel);
        if (res != null) return res;

        if (live.Bucket != null)
        {
            res = PerformLiveBucketQualityChecks(signal, decision, live.Bucket, capitalBias, biasLabel);
            if (res != null) return res;
        }

        var exactBucketPromotionBias = ComputeExactBucketPromotionBias(signal, decision, live);
        res = CheckExactBucketPromotionBias(exactBucketPromotionBias, biasLabel);
        if (res != null) return res;

        var exitPolicyDrift = EvaluateExitPolicyDrift(signal.Symbol, decision.StrategyTag, decision.RegimeTag, ResolveSessionBucket(signal.Timestamp), decision.ExitPolicyTag);
        return CheckExitPolicyDrift(decision, ResolveSessionBucket(signal.Timestamp), exitPolicyDrift, capitalBias, biasLabel);
    }

    private PromotionGuardResult? PerformLiveBucketQualityChecks(
        Signal signal, TradeDecision decision, 
        StrategyBucketPerformanceSnapshot bucket, 
        decimal capitalBias, string biasLabel)
    {
        var profile = _executionProfiles.ResolveForSymbol(signal.Symbol, signal.Metadata);
        var res = CheckEdgeQuality(signal, decision, bucket, capitalBias, biasLabel);
        if (res != null) return res;

        res = CheckHighEdgeOutcomes(signal, decision, bucket, capitalBias, biasLabel);
        if (res != null) return res;

        return CheckHftLatency(decision, bucket, profile, capitalBias, biasLabel);
    }

    private PromotionGuardResult? CheckLadderGates(Signal signal, TradeDecision decision, StrategyBucketPerformanceSnapshot? backtestBucket, StrategyBucketPerformanceSnapshot? paperBucket)
    {
        if (backtestBucket is null || !backtestBucket.BacktestPass)
        {
            return new PromotionGuardResult(
                false,
                0m,
                $"Backtest ladder gate is not passed yet for {signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag}.",
                false,
                0,
                "Backtest");
        }

        // Reject stale backtests older than 14 days — strategy must be re-validated regularly
        if (!string.IsNullOrEmpty(backtestBucket.BacktestVersion) &&
            (DateTime.UtcNow - backtestBucket.RecordedAtUtc).TotalDays > 14)
        {
            return new PromotionGuardResult(
                false,
                0m,
                $"Backtest {backtestBucket.BacktestVersion} is stale ({(DateTime.UtcNow - backtestBucket.RecordedAtUtc).TotalDays:F0} days old). Re-run backtest for {signal.Symbol}.",
                false,
                0,
                "Backtest");
        }

        // Reject if PBO score indicates overfitting
        if (backtestBucket.PboScore > 0.5)
        {
            return new PromotionGuardResult(
                false,
                0m,
                $"Backtest PBO={backtestBucket.PboScore:F2} indicates overfitting for {signal.Symbol} {decision.StrategyTag}. Strategy not robust enough for promotion.",
                false,
                0,
                "Backtest");
        }

        var observedSymbols = _state.BacktestBuckets
            .Select(bucket => bucket.Symbol)
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (observedSymbols >= Math.Max(2, _qualityGate.MinPortfolioReadySymbols) &&
            !_state.PortfolioValidationReady)
        {
            var summary = string.IsNullOrWhiteSpace(_state.PortfolioValidationSummary)
                ? $"Portfolio validation requires {_qualityGate.MinPortfolioReadySymbols} symbols."
                : _state.PortfolioValidationSummary;
            return new PromotionGuardResult(
                false,
                0m,
                $"Portfolio validation gate is not passed yet. {summary}",
                false,
                0,
                "Backtest");
        }

        if (paperBucket is null || !paperBucket.PaperPass)
        {
            return new PromotionGuardResult(
                false,
                0m,
                $"Paper ladder gate is not passed yet for {signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag}.",
                false,
                0,
                "Paper");
        }

        return null;
    }

    private static PromotionGuardResult? CheckLiveSampleDepth(StrategyBucketPerformanceSnapshot? liveBucket, decimal capitalBiasMultiplier, string capitalBiasLabel)
    {
        if (liveBucket is null || liveBucket.Trades < 4)
        {
            return new PromotionGuardResult(
                true,
                ApplyCapitalBiasToStage(0.20m, "LiveProbe", capitalBiasMultiplier),
                $"Promotion guard: paper-ready bucket, but live sample is thin ({liveBucket?.Trades ?? 0} trades). Start smaller. {capitalBiasLabel}",
                true,
                0,
                "LiveProbe");
        }

        return null;
    }

    private static PromotionGuardResult? CheckConcordanceBias(FinalAllocatorBiasPromotionGuard? concordancePromotionBias, decimal capitalBiasMultiplier, string capitalBiasLabel)
    {
        if (concordancePromotionBias.HasValue)
        {
            var concordanceBias = concordancePromotionBias.Value;
            if (!concordanceBias.Approved)
            {
                return new PromotionGuardResult(
                    false,
                    0m,
                    $"{concordanceBias.Reason} {capitalBiasLabel}",
                    false,
                    0,
                    "Paper");
            }

            if (concordanceBias.ForceLiveProbe)
            {
                return new PromotionGuardResult(
                    true,
                    ApplyCapitalBiasToStage(Math.Min(0.20m, concordanceBias.StageMultiplier), "LiveProbe", capitalBiasMultiplier),
                    $"{concordanceBias.Reason} {capitalBiasLabel}",
                    false,
                    0,
                    "LiveProbe");
            }
        }

        return null;
    }

    private static PromotionGuardResult? CheckVenuePromotionBias(FinalAllocatorBiasPromotionGuard? venuePromotionBias, decimal capitalBiasMultiplier, string capitalBiasLabel)
    {
        if (venuePromotionBias.HasValue)
        {
            var venueBias = venuePromotionBias.Value;
            if (!venueBias.Approved)
            {
                return new PromotionGuardResult(
                    false,
                    0m,
                    $"{venueBias.Reason} {capitalBiasLabel}",
                    false,
                    0,
                    "Paper");
            }

            if (venueBias.ForceLiveProbe)
            {
                return new PromotionGuardResult(
                    true,
                    ApplyCapitalBiasToStage(Math.Min(0.25m, venueBias.StageMultiplier), "LiveProbe", capitalBiasMultiplier),
                    $"{venueBias.Reason} {capitalBiasLabel}",
                    false,
                    0,
                    "LiveProbe");
            }
        }

        return null;
    }

    private static PromotionGuardResult? CheckEdgeQuality(Signal signal, TradeDecision decision, StrategyBucketPerformanceSnapshot liveBucket, decimal capitalBiasMultiplier, string capitalBiasLabel)
    {
        if (liveBucket.AverageEdgeScore > 0m)
        {
            if (liveBucket.AverageEdgeScore < 55m)
            {
                return new PromotionGuardResult(
                    false,
                    0m,
                    $"Live edge quality too weak for {signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag}. Avg edge {liveBucket.AverageEdgeScore:F1}.",
                    false,
                    0,
                    "Paper");
            }

            if (liveBucket.AverageEdgeScore < 70m)
            {
                return new PromotionGuardResult(
                    true,
                    ApplyCapitalBiasToStage(0.25m, "LiveProbe", capitalBiasMultiplier),
                    $"Live edge quality is soft for {signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag}. Avg edge {liveBucket.AverageEdgeScore:F1}. Reduce size. {capitalBiasLabel}",
                    false,
                    0,
                    "LiveProbe");
            }
        }

        return null;
    }

    private static PromotionGuardResult? CheckHighEdgeOutcomes(Signal signal, TradeDecision decision, StrategyBucketPerformanceSnapshot liveBucket, decimal capitalBiasMultiplier, string capitalBiasLabel)
    {
        if (liveBucket.HighEdgeTrades >= 3)
        {
            if (liveBucket.HighEdgeExpectedValue <= 0m || liveBucket.HighEdgeProfitFactor < 1.0 || liveBucket.HighEdgeWinRate < 45)
            {
                return new PromotionGuardResult(
                    false,
                    0m,
                    $"High-edge outcomes are failing for {signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag}. Full/Reduced trades: {liveBucket.HighEdgeTrades}, EV={liveBucket.HighEdgeExpectedValue:F2}, PF={liveBucket.HighEdgeProfitFactor:F2}, WR={liveBucket.HighEdgeWinRate:F1}%.",
                    false,
                    0,
                    "Paper");
            }

            if (liveBucket.HighEdgeProfitFactor < 1.20 || liveBucket.HighEdgeWinRate < 52)
            {
                return new PromotionGuardResult(
                    true,
                    ApplyCapitalBiasToStage(0.25m, "LiveProbe", capitalBiasMultiplier),
                    $"High-edge outcomes are still soft for {signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag}. Full/Reduced trades: {liveBucket.HighEdgeTrades}, EV={liveBucket.HighEdgeExpectedValue:F2}, PF={liveBucket.HighEdgeProfitFactor:F2}, WR={liveBucket.HighEdgeWinRate:F1}%. {capitalBiasLabel}",
                    false,
                    0,
                    "LiveProbe");
            }
        }

        return null;
    }

    private static PromotionGuardResult? CheckHftLatency(TradeDecision decision, StrategyBucketPerformanceSnapshot liveBucket, MarketExecutionProfile executionProfile, decimal capitalBiasMultiplier, string capitalBiasLabel)
    {
        if (string.Equals(decision.StrategyTag, "HftMicroEdge", StringComparison.OrdinalIgnoreCase))
        {
            if (liveBucket.AverageExecutionLatencyMs > executionProfile.PreferredMaxLatencyMs * 1.5)
            {
                return new PromotionGuardResult(
                    false,
                    0m,
                    $"HFT promotion blocked: average latency {liveBucket.AverageExecutionLatencyMs:F0}ms exceeds {executionProfile.Venue} budget {executionProfile.PreferredMaxLatencyMs}ms.",
                    false,
                    1.0,
                    "Paper");
            }

            if (liveBucket.AverageExecutionLatencyMs > executionProfile.PreferredMaxLatencyMs)
            {
                return new PromotionGuardResult(
                    true,
                    ApplyCapitalBiasToStage(0.15m, "LiveProbe", capitalBiasMultiplier),
                    $"HFT latency caution: average latency {liveBucket.AverageExecutionLatencyMs:F0}ms is above {executionProfile.Venue} budget {executionProfile.PreferredMaxLatencyMs}ms. {capitalBiasLabel}",
                    false,
                    0.55,
                    "LiveProbe");
            }
        }

        return null;
    }

    private static PromotionGuardResult? CheckMlDrift(IReadOnlyList<Trade> liveTrades, decimal capitalBiasMultiplier, string capitalBiasLabel)
    {
        if (liveTrades == null || liveTrades.Count == 0) return null;

        int driftCount = 0;
        int criticalCount = 0;
        string modelVersion = "Unknown";

        // Examine the recent execution window
        foreach (var t in liveTrades.TakeLast(10))
        {
            if (t.FeatureDriftDetected) driftCount++;
            if (t.FeatureDriftCritical) criticalCount++;
            if (!string.IsNullOrEmpty(t.MlModelVersion)) modelVersion = t.MlModelVersion;
        }

        // Severe regime shift: immediately pause live full-size execution
        if (criticalCount >= 2)
        {
            return new PromotionGuardResult(
                false,
                0m,
                $"ML drift is blocking promotion. Critical feature anomalies detected in {modelVersion}. {capitalBiasLabel}",
                false,
                0,
                "Paper");
        }

        // Soft drift: keep taking live probes, but slash capital allocation severely
        if (driftCount >= 3)
        {
            return new PromotionGuardResult(
                true,
                ApplyCapitalBiasToStage(0.15m, "LiveProbe", capitalBiasMultiplier), // Max 15% size
                $"ML drift is holding size. Model features are drifting against {modelVersion}. {capitalBiasLabel}",
                false,
                0,
                "LiveProbe");
        }

        return null;
    }

    private static PromotionGuardResult? CheckExactBucketPromotionBias(FinalAllocatorBiasPromotionGuard? exactBucketPromotionBias, string capitalBiasLabel)
    {
        if (exactBucketPromotionBias.HasValue)
        {
            var exactBias = exactBucketPromotionBias.Value;
            if (!exactBias.Approved)
            {
                return new PromotionGuardResult(
                    false,
                    0m,
                    $"{exactBias.Reason} {capitalBiasLabel}",
                    false,
                    0,
                    "Paper");
            }
        }

        return null;
    }

    private static PromotionGuardResult? CheckExitPolicyDrift(TradeDecision decision, string sessionTag, ExitPolicyDriftSnapshot? exitPolicyDrift, decimal capitalBiasMultiplier, string capitalBiasLabel)
    {
        if (exitPolicyDrift.HasValue)
        {
            var drift = exitPolicyDrift.Value;
            if (drift.Score >= 0.70 && (drift.LiveExpectedValue <= 0m || drift.LiveProfitFactor < 0.95 || drift.LiveWinRate < 42))
            {
                return new PromotionGuardResult(
                    false,
                    0m,
                    $"Exit-policy drift too large for {decision.ExitPolicyTag} on {sessionTag}. Drift={drift.Score:F2}, paper EV={drift.PaperExpectedValue:F2}, live EV={drift.LiveExpectedValue:F2}, paper PF={drift.PaperProfitFactor:F2}, live PF={drift.LiveProfitFactor:F2}.",
                    false,
                    drift.Score,
                    "Paper");
            }

            if (drift.Score >= 0.45)
            {
                return new PromotionGuardResult(
                    true,
                    ApplyCapitalBiasToStage(0.20m, "LiveProbe", capitalBiasMultiplier),
                    $"Exit-policy drift caution for {decision.ExitPolicyTag} on {sessionTag}. Drift={drift.Score:F2}, paper EV={drift.PaperExpectedValue:F2}, live EV={drift.LiveExpectedValue:F2}, paper PF={drift.PaperProfitFactor:F2}, live PF={drift.LiveProfitFactor:F2}. {capitalBiasLabel}",
                    false,
                    drift.Score,
                    "LiveProbe");
            }
        }

        return null;
    }

    private static PromotionGuardResult? CheckPaperLiveDrift(string sessionTag, StrategyBucketPerformanceSnapshot liveBucket, double driftScore, decimal capitalBiasMultiplier, string capitalBiasLabel)
    {
        if ((driftScore >= 0.75 && liveBucket.ExpectedValue <= 0) ||
            (driftScore >= 0.60 && (liveBucket.ExpectedValue <= 0 || liveBucket.ProfitFactor < 0.75)))
        {
            return new PromotionGuardResult(
                false,
                0m,
                $"Paper/live drift too large for {sessionTag}. Drift={driftScore:F2}.",
                false,
                driftScore,
                "Paper");
        }

        if (driftScore >= 0.40 || liveBucket.ExpectedValue <= 0 || liveBucket.ProfitFactor < 1.0)
        {
            return new PromotionGuardResult(
                true,
                ApplyCapitalBiasToStage(0.25m, "LiveProbe", capitalBiasMultiplier),
                $"Paper/live drift caution: drift={driftScore:F2}, live EV={liveBucket.ExpectedValue:F2}, PF={liveBucket.ProfitFactor:F2}, slip={liveBucket.AverageEntrySlippagePercent:F3}%, latency={liveBucket.AverageExecutionLatencyMs:F0}ms. {capitalBiasLabel}",
                false,
                driftScore,
                "LiveProbe");
        }

        return null;
    }

    private static PromotionGuardResult CreateFinalPromotionResult(StrategyBucketPerformanceSnapshot liveBucket, FinalAllocatorBiasPromotionGuard? concordancePromotionBias, decimal capitalBiasMultiplier, string capitalBiasLabel, double driftScore)
    {
        return new PromotionGuardResult(
            true,
            ApplyCapitalBiasToStage(
                (liveBucket.ScaledLiveEligible && liveBucket.AverageEdgeScore >= 85m && liveBucket.HighEdgeProfitFactor >= 1.35 ? 1.10m : 1.00m)
                * (concordancePromotionBias?.StageMultiplier ?? 1.00m),
                "ScaledLive",
                capitalBiasMultiplier),
            $"Promotion guard confirmed. Drift={driftScore:F2}, live EV={liveBucket.ExpectedValue:F2}, PF={liveBucket.ProfitFactor:F2}, slip={liveBucket.AverageEntrySlippagePercent:F3}%, latency={liveBucket.AverageExecutionLatencyMs:F0}ms.{BuildConcordanceSuffix(concordancePromotionBias)} {capitalBiasLabel}",
            true,
            driftScore,
            "ScaledLive");
    }

    public FinalAllocatorBiasResult EvaluateFinalAllocatorBias(Signal signal, TradeDecision decision, IReadOnlyList<Trade> liveTrades)
    {
        lock (_sync)
        {
            RefreshLiveState(liveTrades);

            var sessionTag = ResolveSessionBucket(signal.Timestamp);
            var backtestMatch = FindBestBucketMatch(_state.BacktestBuckets, signal.Symbol, decision.StrategyTag, decision.RegimeTag, sessionTag);
            var paperMatch = FindBestBucketMatch(_state.PaperBuckets, signal.Symbol, decision.StrategyTag, decision.RegimeTag, sessionTag);
            var liveMatch = FindBestBucketMatch(_state.LiveBuckets, signal.Symbol, decision.StrategyTag, decision.RegimeTag, sessionTag);
            var capitalBiasMultiplier = ComputeCapitalBiasMultiplier(backtestMatch, paperMatch, liveMatch);
            var baseFinalMultiplier = Math.Clamp(0.90m + ((capitalBiasMultiplier - 0.75m) / 0.45m) * 0.22m, 0.90m, 1.12m);
            var scope = BuildCapitalBiasScope(backtestMatch, paperMatch, liveMatch);
            var exitBias = ComputeFinalExitPolicyBias(signal.Symbol, decision.StrategyTag, decision.RegimeTag, sessionTag, decision.ExitPolicyTag);
            var allocatorLearningBias = ComputeAllocatorLearningBias(liveMatch);
            var exactBucketSizingBias = ComputeExactBucketSizingBias(signal, decision, liveMatch);
            var venueExecutionBias = ComputeVenueExecutionBias(signal, liveMatch);
            var concordanceBias = ComputeExactVenueConcordanceSizingBias(signal, decision, exactBucketSizingBias, venueExecutionBias);
            var finalMultiplier = Math.Clamp(
                baseFinalMultiplier * exitBias.SizeMultiplier * allocatorLearningBias.SizeMultiplier * exactBucketSizingBias.SizeMultiplier * venueExecutionBias.SizeMultiplier * concordanceBias.SizeMultiplier,
                0.82m,
                1.15m);

            var reasonParts = new List<string> { $"Final allocator bias {finalMultiplier:F2}x from {scope}." };
            if (exitBias.SizeMultiplier != 1.0m && !string.IsNullOrWhiteSpace(exitBias.Reason))
            {
                reasonParts.Add($"Exit overlay: {exitBias.Reason}");
            }

            if (allocatorLearningBias.SizeMultiplier != 1.0m && !string.IsNullOrWhiteSpace(allocatorLearningBias.Reason))
            {
                reasonParts.Add($"Allocation learning: {allocatorLearningBias.Reason}");
            }

            if (exactBucketSizingBias.SizeMultiplier != 1.0m && !string.IsNullOrWhiteSpace(exactBucketSizingBias.Reason))
            {
                reasonParts.Add($"Exact-bucket learning: {exactBucketSizingBias.Reason}");
            }

            if (venueExecutionBias.SizeMultiplier != 1.0m && !string.IsNullOrWhiteSpace(venueExecutionBias.Reason))
            {
                reasonParts.Add($"Venue overlay: {venueExecutionBias.Reason}");
            }

            if (concordanceBias.SizeMultiplier != 1.0m && !string.IsNullOrWhiteSpace(concordanceBias.Reason))
            {
                reasonParts.Add($"Concordance overlay: {concordanceBias.Reason}");
            }

            var reason = string.Join(" ", reasonParts);

            return new FinalAllocatorBiasResult(
                finalMultiplier,
                reason,
                scope,
                venueExecutionBias.SizeMultiplier,
                venueExecutionBias.Scope);
        }
    }

    public async Task RecordBacktestState(BacktestResult result)
    {
        lock (_sync)
        {
            var tradeEntities = result.Trades.Select(MapToTrade).ToList();

            var snapshots = tradeEntities
                .GroupBy(trade => new
                {
                    result.Symbol,
                    StrategyTag = string.IsNullOrWhiteSpace(trade.StrategyTag) ? "TrendConfluence" : trade.StrategyTag,
                    RegimeTag = string.IsNullOrWhiteSpace(trade.RegimeTag) ? "Unknown" : trade.RegimeTag,
                    SessionTag = ResolveSessionBucket(trade.ExitTime ?? DateTime.UtcNow)
                })
                .Select(group => CreateBacktestBucketSnapshot(group.Key.Symbol, group.Key.StrategyTag, group.Key.RegimeTag, group.Key.SessionTag, group.ToList()))
                .ToList();

            var exitPolicySnapshots = RefreshExitPolicySnapshots(tradeEntities, result.Symbol, "SIMULATED");

            UpdateBuckets(_state.BacktestBuckets, snapshots);
            UpsertExitPolicySnapshots(exitPolicySnapshots);
            RefreshPortfolioValidationState();
        }

        await PersistAsync();
    }

    /// <summary>
    /// Returns a snapshot of the promotion ladder state for each strategy bucket,
    /// for dashboard/monitoring. No mutation, no side effects.
    /// </summary>
    public IReadOnlyList<PromotionLadderSnapshot> GetPromotionLadder()
    {
        lock (_sync)
        {
            var buckets = new Dictionary<string, PromotionLadderSnapshot>(StringComparer.OrdinalIgnoreCase);
            void Add(StrategyBucketPerformanceSnapshot bucket, string stage)
            {
                var key = $"{bucket.Symbol}|{bucket.StrategyTag}|{bucket.RegimeTag}|{bucket.SessionTag}";
                if (!buckets.TryGetValue(key, out var snap))
                {
                    snap = new PromotionLadderSnapshot
                    {
                        Symbol = bucket.Symbol,
                        StrategyTag = bucket.StrategyTag,
                        RegimeTag = bucket.RegimeTag,
                        SessionTag = bucket.SessionTag,
                    };
                    buckets[key] = snap;
                }
                switch (stage)
                {
                    case "Backtest": snap.BacktestPass = bucket.BacktestPass; snap.BacktestTrades = bucket.Trades; snap.BacktestPF = bucket.ProfitFactor; break;
                    case "Paper": snap.PaperPass = bucket.PaperPass; snap.PaperTrades = bucket.Trades; snap.PaperPF = bucket.ProfitFactor; snap.PaperWinRate = bucket.WinRate; break;
                    case "Live": snap.LiveEligible = bucket.ScaledLiveEligible; snap.LiveTrades = bucket.Trades; snap.LivePF = bucket.ProfitFactor; snap.LiveEdgeScore = bucket.AverageEdgeScore; break;
                }
            }

            foreach (var b in _state.BacktestBuckets) Add(b, "Backtest");
            foreach (var b in _state.PaperBuckets) Add(b, "Paper");
            foreach (var b in _state.LiveBuckets) Add(b, "Live");

            foreach (var snap in buckets.Values)
            {
                snap.CurrentStage = snap.LiveEligible ? "ScaledLive"
                    : snap.PaperPass ? "LiveProbe"
                    : snap.BacktestPass ? "Paper"
                    : "Backtest";
                snap.NextStageEligible = snap.CurrentStage switch
                {
                    "Backtest" => snap.BacktestPass,
                    "Paper" => snap.PaperPass,
                    "LiveProbe" => snap.LiveEligible,
                    _ => false,
                };
            }

            return buckets.Values.OrderBy(s => s.Symbol).ThenBy(s => s.StrategyTag).ToList();
        }
    }

    public async Task RecordPaperState(IEnumerable<PaperPosition> closedPositions)
    {
        var closedList = closedPositions.Where(p => p.ExitTime.HasValue).ToList();

        lock (_sync)
        {
            var paperSnapshots = closedList
                .GroupBy(p => new { p.Symbol, p.StrategyTag, p.RegimeTag, SessionTag = ResolveSessionBucket(p.ExitTime!.Value) })
                .Select(group => CreatePaperBucketSnapshot(group.Key.Symbol, group.Key.StrategyTag, group.Key.RegimeTag, group.Key.SessionTag, group.ToList()))
                .ToList();

            var exitPolicySnapshots = RefreshExitPolicySnapshotsFromPaper(closedList);

            UpdateBuckets(_state.PaperBuckets, paperSnapshots);
            UpsertExitPolicySnapshots(exitPolicySnapshots);
        }

        await PersistAsync();
    }

    private StrategyBucketPerformanceSnapshot CreateBacktestBucketSnapshot(string symbol, string strategy, string regime, string session, List<Trade> trades)
    {
        var pnlValues = trades.Select(GetLivePnl).ToList();
        var percentReturns = trades.Select(t => t.EntryPrice * t.Quantity > 0 ? GetLivePnl(t) / (t.EntryPrice * t.Quantity) * 100m : 0m).ToList();
        var equityCurve = new List<decimal> { 100_000m };
        foreach (var pnl in pnlValues) equityCurve.Add(equityCurve.Last() + pnl);
        var maxDrawdownPercent = ComputeMaxDrawdownPercent(equityCurve);
        var sharpeRatio = ComputeSharpeRatio(percentReturns);
        var expectancy = pnlValues.Count == 0 ? 0m : pnlValues.Average();

        var pass = pnlValues.Count >= _backtestMinTrades
            && (double)ComputeProfitFactor(pnlValues) >= _backtestMinProfitFactor
            && expectancy >= _qualityGate.MinExpectancyPerTrade
            && maxDrawdownPercent <= _qualityGate.MaxDrawdownPercent
            && sharpeRatio >= Math.Min(_qualityGate.ShortMinSharpe, _qualityGate.LongMinSharpe)
            && trades.Count(t => GetLivePnl(t) > 0) * 1.0 / Math.Max(1, trades.Count) >= _backtestMinWinRate;

        return new StrategyBucketPerformanceSnapshot
        {
            Symbol = symbol, StrategyTag = strategy, RegimeTag = regime, SessionTag = session,
            Trades = pnlValues.Count, WinRate = pnlValues.Count == 0 ? 0 : pnlValues.Count(v => v > 0) * 100.0 / pnlValues.Count,
            ProfitFactor = ComputeProfitFactor(pnlValues), ExpectedValue = expectancy,
            TotalPnL = pnlValues.Sum(), UlcerIndex = ComputeUlcerIndex(equityCurve), SortinoRatio = ComputeSortinoRatio(percentReturns),
            SharpeRatio = sharpeRatio, MaxDrawdownPercent = (double)maxDrawdownPercent,
            BacktestPass = pass, RecordedAtUtc = DateTime.UtcNow, PromotionReady = pass, PromotionStage = "Backtest"
        };
    }

    private StrategyBucketPerformanceSnapshot CreatePaperBucketSnapshot(string symbol, string strategy, string regime, string session, List<PaperPosition> positions)
    {
        var pnlValues = positions.Select(p => p.RealizedPnL).ToList();
        var percentReturns = positions.Select(p => p.EntryPrice * p.Quantity > 0 ? p.RealizedPnL / (p.EntryPrice * p.Quantity) * 100m : 0m).ToList();
        var equityCurve = new List<decimal> { 100_000m };
        foreach (var pnl in pnlValues) equityCurve.Add(equityCurve.Last() + pnl);

        var backtestMatch = FindBestBucketMatch(_state.BacktestBuckets, symbol, strategy, regime, session).Bucket;
        var backtestPass = backtestMatch?.BacktestPass == true;
        var paperPass = backtestPass
            && pnlValues.Count >= _paperMinTrades
            && (double)ComputeProfitFactor(pnlValues) >= _paperMinProfitFactor
            && pnlValues.Average() > 0
            && pnlValues.Count(v => v > 0) * 1.0 / pnlValues.Count >= _paperMinWinRate;
        return new StrategyBucketPerformanceSnapshot
        {
            Symbol = symbol, StrategyTag = strategy, RegimeTag = regime, SessionTag = session,
            Trades = pnlValues.Count, WinRate = pnlValues.Count == 0 ? 0 : pnlValues.Count(v => v > 0) * 100.0 / pnlValues.Count,
            ProfitFactor = ComputeProfitFactor(pnlValues), ExpectedValue = pnlValues.Count == 0 ? 0 : pnlValues.Average(),
            TotalPnL = pnlValues.Sum(), UlcerIndex = ComputeUlcerIndex(equityCurve), SortinoRatio = ComputeSortinoRatio(percentReturns),
            BacktestPass = backtestPass, PaperPass = paperPass,
            PromotionReady = paperPass, PromotionStage = paperPass ? "Paper" : "Backtest", RecordedAtUtc = DateTime.UtcNow
        };
    }

    private List<ExitPolicyPerformanceSnapshot> RefreshExitPolicySnapshots(IEnumerable<Trade> trades, string symbol, string fallbackMode)
    {
        return trades
            .GroupBy(t => new { symbol, StrategyTag = string.IsNullOrWhiteSpace(t.StrategyTag) ? "TrendConfluence" : t.StrategyTag, RegimeTag = string.IsNullOrWhiteSpace(t.RegimeTag) ? "Unknown" : t.RegimeTag, SessionTag = ResolveSessionBucket(t.ExitTime ?? DateTime.UtcNow), ExitPolicyTag = string.IsNullOrWhiteSpace(t.ExitPolicyTag) ? "TrendRunner" : t.ExitPolicyTag, ExecutionMode = string.IsNullOrWhiteSpace(t.ExecutionMode) ? fallbackMode : t.ExecutionMode })
            .Select(g => new ExitPolicyPerformanceSnapshot { Symbol = g.Key.symbol, StrategyTag = g.Key.StrategyTag, RegimeTag = g.Key.RegimeTag, SessionTag = g.Key.SessionTag, ExitPolicyTag = g.Key.ExitPolicyTag, ExecutionMode = g.Key.ExecutionMode, Trades = g.Count(), WinRate = !g.Any() ? 0 : g.Count(item => GetLivePnl(item) > 0) * 100.0 / g.Count(), ProfitFactor = ComputeProfitFactor(g.Select(GetLivePnl)), ExpectedValue = g.Any() ? g.Average(GetLivePnl) : 0m, TotalPnL = g.Sum(GetLivePnl), RecordedAtUtc = DateTime.UtcNow })
            .ToList();
    }

    private List<ExitPolicyPerformanceSnapshot> RefreshExitPolicySnapshotsFromPaper(List<PaperPosition> positions)
    {
        return positions
            .GroupBy(p => new { p.Symbol, p.StrategyTag, p.RegimeTag, SessionTag = ResolveSessionBucket(p.ExitTime!.Value), ExitPolicyTag = string.IsNullOrWhiteSpace(p.ExitPolicyTag) ? "TrendRunner" : p.ExitPolicyTag, ExecutionMode = string.IsNullOrWhiteSpace(p.ExecutionMode) ? "PAPER" : p.ExecutionMode })
            .Select(g => new ExitPolicyPerformanceSnapshot { Symbol = g.Key.Symbol, StrategyTag = g.Key.StrategyTag, RegimeTag = g.Key.RegimeTag, SessionTag = g.Key.SessionTag, ExitPolicyTag = g.Key.ExitPolicyTag, ExecutionMode = g.Key.ExecutionMode, Trades = g.Count(), WinRate = !g.Any() ? 0 : g.Count(item => item.RealizedPnL > 0) * 100.0 / g.Count(), ProfitFactor = ComputeProfitFactor(g.Select(item => item.RealizedPnL)), ExpectedValue = g.Any() ? g.Average(item => item.RealizedPnL) : 0m, TotalPnL = g.Sum(item => item.RealizedPnL), RecordedAtUtc = DateTime.UtcNow })
            .ToList();
    }

    private static void UpdateBuckets(List<StrategyBucketPerformanceSnapshot> target, List<StrategyBucketPerformanceSnapshot> source)
    {
        foreach (var snapshot in source)
        {
            target.RemoveAll(existing =>
                string.Equals(existing.Symbol, snapshot.Symbol, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.StrategyTag, snapshot.StrategyTag, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.RegimeTag, snapshot.RegimeTag, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.SessionTag, snapshot.SessionTag, StringComparison.OrdinalIgnoreCase));
            target.Add(snapshot);
        }
    }


    public StrategyPerformanceState GetState()
    {
        lock (_sync)
        {
            return _state.Clone();
        }
    }

    private void RefreshLiveState(IReadOnlyList<Trade> liveTrades)
    {
        _state.LiveBuckets = liveTrades
            .Where(trade => trade.IsClosed)
            .GroupBy(trade => new
            {
                trade.Symbol,
                trade.StrategyTag,
                trade.RegimeTag,
                SessionTag = ResolveSessionBucket(trade.ExitTime!.Value)
            })
            .Select(group => CreateBucketSnapshot(group.Key.Symbol, group.Key.StrategyTag, group.Key.RegimeTag, group.Key.SessionTag, group.ToList()))
            .OrderByDescending(snapshot => snapshot.RecordedAtUtc)
            .ToList();

        UpsertExitPolicySnapshots(
            liveTrades
                .Where(trade => trade.IsClosed)
                .GroupBy(trade => new
                {
                    trade.Symbol,
                    trade.StrategyTag,
                    trade.RegimeTag,
                    SessionTag = ResolveSessionBucket(trade.ExitTime!.Value),
                    ExitPolicyTag = string.IsNullOrWhiteSpace(trade.ExitPolicyTag) ? "TrendRunner" : trade.ExitPolicyTag.Trim(),
                    ExecutionMode = string.IsNullOrWhiteSpace(trade.ExecutionMode) ? "REAL" : trade.ExecutionMode.Trim()
                })
                .Select(group => CreateExitPolicySnapshot(group.Key.Symbol, group.Key.StrategyTag, group.Key.RegimeTag, group.Key.SessionTag, group.Key.ExitPolicyTag, group.Key.ExecutionMode, group.ToList()))
                .ToList());
    }

    private StrategyBucketPerformanceSnapshot CreateBucketSnapshot(string symbol, string strategyTag, string regimeTag, string sessionTag, List<Trade> trades)
    {
        var pnlValues = trades.Select(GetLivePnl).ToList();
        var backtestMatch = FindBestBucketMatch(_state.BacktestBuckets, symbol, strategyTag, regimeTag, sessionTag).Bucket;
        var paperMatch = FindBestBucketMatch(_state.PaperBuckets, symbol, strategyTag, regimeTag, sessionTag).Bucket;
        
        var backtestPass = backtestMatch?.BacktestPass == true;
        var paperPass = paperMatch?.PaperPass == true;

        var edgeMetrics = CalculateEdgePerformance(trades);
        var allocatorMetrics = CalculateAllocatorPerformance(trades);
        var executionMetrics = CalculateExecutionPerformance(trades);
        
        var liveProbePass = pnlValues.Count >= 4 &&
                            backtestPass &&
                            paperPass &&
                            ComputeProfitFactor(pnlValues) >= 1.0 &&
                            pnlValues.Average() >= 0 &&
                            (edgeMetrics.HighEdgeTrades < 3 || (edgeMetrics.HighEdgeProfitFactor >= 1.0 && edgeMetrics.HighEdgeExpectedValue >= 0));
                            
        var scaledLiveEligible = pnlValues.Count >= 8 &&
                                 backtestPass &&
                                 paperPass &&
                                 ComputeProfitFactor(pnlValues) >= 1.20 &&
                                 pnlValues.Average() > 0 &&
                                 (pnlValues.Count == 0 ? 0 : pnlValues.Count(v => v > 0) * 1.0 / pnlValues.Count) >= 0.45 &&
                                 (edgeMetrics.AverageEdgeScore == 0m || edgeMetrics.AverageEdgeScore >= 70m) &&
                                 (edgeMetrics.HighEdgeTrades < 3 || (edgeMetrics.HighEdgeProfitFactor >= 1.10 && edgeMetrics.HighEdgeExpectedValue > 0 && edgeMetrics.HighEdgeWinRate >= 45));

        var snapshot = new StrategyBucketPerformanceSnapshot
        {
            Symbol = symbol,
            StrategyTag = strategyTag,
            RegimeTag = regimeTag,
            SessionTag = sessionTag,
            Trades = pnlValues.Count,
            WinRate = pnlValues.Count == 0 ? 0 : pnlValues.Count(v => v > 0) * 100.0 / pnlValues.Count,
            ProfitFactor = ComputeProfitFactor(pnlValues),
            ExpectedValue = pnlValues.Count == 0 ? 0 : pnlValues.Average(),
            TotalPnL = pnlValues.Sum(),
            UlcerIndex = ComputeUlcerIndex(CreateEquityCurve(pnlValues)),
            SortinoRatio = ComputeSortinoRatio(trades.Select(t => t.EntryPrice * t.Quantity > 0 ? GetLivePnl(t) / (t.EntryPrice * t.Quantity) * 100m : 0m)),
            AverageEdgeScore = edgeMetrics.AverageEdgeScore,
            HighEdgeTrades = edgeMetrics.HighEdgeTrades,
            HighEdgeWinRate = edgeMetrics.HighEdgeWinRate,
            HighEdgeProfitFactor = edgeMetrics.HighEdgeProfitFactor,
            HighEdgeExpectedValue = edgeMetrics.HighEdgeExpectedValue,
            HighEdgeTotalPnL = edgeMetrics.HighEdgeTotalPnL,
            BacktestPass = backtestPass,
            PaperPass = paperPass,
            LiveProbePass = liveProbePass,
            ScaledLiveEligible = scaledLiveEligible,
            PromotionReady = scaledLiveEligible,
            PromotionStage = DetermineLivePromotionStage(pnlValues.Count, scaledLiveEligible, liveProbePass),
            RecordedAtUtc = DateTime.UtcNow
        };

        PopulateAllocatorMetrics(snapshot, allocatorMetrics);
        PopulateExecutionMetrics(snapshot, executionMetrics);
        
        return snapshot;
    }

    private static (decimal AverageEdgeScore, int HighEdgeTrades, double HighEdgeWinRate, double HighEdgeProfitFactor, decimal HighEdgeExpectedValue, decimal HighEdgeTotalPnL) CalculateEdgePerformance(List<Trade> trades)
    {
        var highEdgePnls = trades
            .Where(trade => IsHighEdgeBand(trade.EdgeBand, trade.EdgeScore))
            .Select(GetLivePnl)
            .ToList();

        return (
            trades.Any(t => t.EdgeScore > 0m) ? trades.Where(t => t.EdgeScore > 0m).Average(t => t.EdgeScore) : 0m,
            highEdgePnls.Count,
            highEdgePnls.Count == 0 ? 0 : highEdgePnls.Count(v => v > 0) * 100.0 / highEdgePnls.Count,
            ComputeProfitFactor(highEdgePnls),
            highEdgePnls.Count == 0 ? 0 : highEdgePnls.Average(),
            highEdgePnls.Sum()
        );
    }

    private record AllocatorPerformance(
        decimal AverageBias,
        int BoostedTrades, double BoostedWinRate, double BoostedProfitFactor, decimal BoostedExpectedValue,
        int TrimmedTrades, double TrimmedWinRate, double TrimmedProfitFactor, decimal TrimmedExpectedValue);

    private static AllocatorPerformance CalculateAllocatorPerformance(List<Trade> trades)
    {
        var boostedPnls = trades.Where(t => t.FinalAllocatorBias > 1.01m).Select(GetLivePnl).ToList();
        var trimmedPnls = trades.Where(t => t.FinalAllocatorBias > 0m && t.FinalAllocatorBias < 0.99m).Select(GetLivePnl).ToList();

        return new AllocatorPerformance(
            trades.Any(t => t.FinalAllocatorBias > 0m) ? trades.Where(t => t.FinalAllocatorBias > 0m).Average(t => t.FinalAllocatorBias) : 0m,
            boostedPnls.Count,
            boostedPnls.Count == 0 ? 0 : boostedPnls.Count(v => v > 0) * 100.0 / boostedPnls.Count,
            ComputeProfitFactor(boostedPnls),
            boostedPnls.Count == 0 ? 0 : boostedPnls.Average(),
            trimmedPnls.Count,
            trimmedPnls.Count == 0 ? 0 : trimmedPnls.Count(v => v > 0) * 100.0 / trimmedPnls.Count,
            ComputeProfitFactor(trimmedPnls),
            trimmedPnls.Count == 0 ? 0 : trimmedPnls.Average()
        );
    }

    private record ExecutionPerformance(
        decimal AverageBias,
        int BoostedTrades, double BoostedWinRate, double BoostedProfitFactor, decimal BoostedExpectedValue,
        int TrimmedTrades, double TrimmedWinRate, double TrimmedProfitFactor, decimal TrimmedExpectedValue,
        decimal AverageSlippage, double AverageLatency);

    private static ExecutionPerformance CalculateExecutionPerformance(List<Trade> trades)
    {
        var boostedPnls = trades.Where(t => t.VenueExecutionBias > 1.01m).Select(GetLivePnl).ToList();
        var trimmedPnls = trades.Where(t => t.VenueExecutionBias > 0m && t.VenueExecutionBias < 0.99m).Select(GetLivePnl).ToList();

        return new ExecutionPerformance(
            trades.Any(t => t.VenueExecutionBias > 0m) ? trades.Where(t => t.VenueExecutionBias > 0m).Average(t => t.VenueExecutionBias) : 0m,
            boostedPnls.Count,
            boostedPnls.Count == 0 ? 0 : boostedPnls.Count(v => v > 0) * 100.0 / boostedPnls.Count,
            ComputeProfitFactor(boostedPnls),
            boostedPnls.Count == 0 ? 0 : boostedPnls.Average(),
            trimmedPnls.Count,
            trimmedPnls.Count == 0 ? 0 : trimmedPnls.Count(v => v > 0) * 100.0 / trimmedPnls.Count,
            ComputeProfitFactor(trimmedPnls),
            trimmedPnls.Count == 0 ? 0 : trimmedPnls.Average(),
            trades.Any() ? trades.Average(t => t.EntrySlippagePercent) : 0m,
            trades.Any() ? trades.Average(t => (double)t.ExecutionLatencyMs) : 0
        );
    }

    private static void PopulateAllocatorMetrics(StrategyBucketPerformanceSnapshot snapshot, AllocatorPerformance metrics)
    {
        snapshot.AverageFinalAllocatorBias = metrics.AverageBias;
        snapshot.BoostedAllocatorTrades = metrics.BoostedTrades;
        snapshot.BoostedAllocatorWinRate = metrics.BoostedWinRate;
        snapshot.BoostedAllocatorProfitFactor = metrics.BoostedProfitFactor;
        snapshot.BoostedAllocatorExpectedValue = metrics.BoostedExpectedValue;
        snapshot.TrimmedAllocatorTrades = metrics.TrimmedTrades;
        snapshot.TrimmedAllocatorWinRate = metrics.TrimmedWinRate;
        snapshot.TrimmedAllocatorProfitFactor = metrics.TrimmedProfitFactor;
        snapshot.TrimmedAllocatorExpectedValue = metrics.TrimmedExpectedValue;
    }

    private static void PopulateExecutionMetrics(StrategyBucketPerformanceSnapshot snapshot, ExecutionPerformance metrics)
    {
        snapshot.AverageVenueExecutionBias = metrics.AverageBias;
        snapshot.BoostedVenueExecutionTrades = metrics.BoostedTrades;
        snapshot.BoostedVenueExecutionWinRate = metrics.BoostedWinRate;
        snapshot.BoostedVenueExecutionProfitFactor = metrics.BoostedProfitFactor;
        snapshot.BoostedVenueExecutionExpectedValue = metrics.BoostedExpectedValue;
        snapshot.TrimmedVenueExecutionTrades = metrics.TrimmedTrades;
        snapshot.TrimmedVenueExecutionWinRate = metrics.TrimmedWinRate;
        snapshot.TrimmedVenueExecutionProfitFactor = metrics.TrimmedProfitFactor;
        snapshot.TrimmedVenueExecutionExpectedValue = metrics.TrimmedExpectedValue;
        snapshot.AverageEntrySlippagePercent = metrics.AverageSlippage;
        snapshot.AverageExecutionLatencyMs = metrics.AverageLatency;
    }


    private ExitPolicyPerformanceSnapshot CreateExitPolicySnapshot(string symbol, string strategyTag, string regimeTag, string sessionTag, string policy, string mode, List<Trade> trades)
    {
        var pnlValues = trades.Select(GetLivePnl).ToList();
        var percentReturns = trades.Select(t => t.EntryPrice * t.Quantity > 0 ? GetLivePnl(t) / (t.EntryPrice * t.Quantity) * 100m : 0m).ToList();
        
        return new ExitPolicyPerformanceSnapshot
        {
            Symbol = symbol,
            StrategyTag = strategyTag,
            RegimeTag = regimeTag,
            SessionTag = sessionTag,
            ExitPolicyTag = policy,
            ExecutionMode = mode,
            Trades = trades.Count,
            WinRate = trades.Count == 0 ? 0 : trades.Count(t => GetLivePnl(t) > 0) * 100.0 / trades.Count,
            ProfitFactor = ComputeProfitFactor(pnlValues),
            ExpectedValue = trades.Any() ? trades.Average(GetLivePnl) : 0m,
            TotalPnL = pnlValues.Sum(),
            UlcerIndex = ComputeUlcerIndex(CreateEquityCurve(pnlValues)),
            SortinoRatio = ComputeSortinoRatio(percentReturns),
            RecordedAtUtc = DateTime.UtcNow
        };
    }

    private static IEnumerable<decimal> CreateEquityCurve(IEnumerable<decimal> pnls)
    {
        decimal equity = 100_000m;
        yield return equity;
        foreach (var pnl in pnls)
        {
            equity += pnl;
            yield return equity;
        }
    }

    public async Task RecordLiveState(IEnumerable<Trade> closedTrades)
    {
        var tradeList = closedTrades.ToList();
        lock (_sync)
        {
            RefreshLiveState(tradeList);
        }
        await PersistAsync();
    }

    private async Task PersistAsync()
    {
        StrategyPerformanceState snapshot;
        lock (_sync)
        {
            _state.RecordedAtUtc = DateTime.UtcNow;
            snapshot = _state.Clone();
        }
        await _store.SaveAsync(snapshot);
    }

    private void UpsertExitPolicySnapshots(IEnumerable<ExitPolicyPerformanceSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            _state.ExitPolicyBuckets.RemoveAll(existing =>
                string.Equals(existing.Symbol, snapshot.Symbol, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.StrategyTag, snapshot.StrategyTag, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.RegimeTag, snapshot.RegimeTag, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.SessionTag, snapshot.SessionTag, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.ExitPolicyTag, snapshot.ExitPolicyTag, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.ExecutionMode, snapshot.ExecutionMode, StringComparison.OrdinalIgnoreCase));
            _state.ExitPolicyBuckets.Add(snapshot);
        }

        _state.ExitPolicyBuckets = _state.ExitPolicyBuckets
            .OrderByDescending(snapshot => snapshot.RecordedAtUtc)
            .ToList();
    }

    private BucketMatch FindBestBucketMatch(
        IEnumerable<StrategyBucketPerformanceSnapshot> buckets,
        string symbol,
        string strategyTag,
        string regimeTag,
        string sessionTag)
    {
        // Specificity 4: exact Symbol + Strategy + Regime + Session.
        var exact = buckets.FirstOrDefault(bucket =>
            string.Equals(bucket.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(bucket.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(bucket.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(bucket.SessionTag, sessionTag, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return new BucketMatch(exact, 4);
        }

        // Specificity 3: same Symbol, drop Session. Symbol-safe.
        var symbolBroad = buckets.FirstOrDefault(bucket =>
            string.Equals(bucket.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(bucket.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(bucket.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase));
        if (symbolBroad is not null)
        {
            return new BucketMatch(symbolBroad, 3);
        }

        // Specificity 2 and below aggregate ACROSS SYMBOLS. Under StrictSymbolIsolation
        // (default for paper/live multi-symbol portfolios) we refuse the fallback so
        // Symbol B's promotion decision cannot inherit Symbol A's performance —
        // callers get an empty match and rely on signal confidence + fresh evidence.
        if (_promotionOptions.StrictSymbolIsolation)
        {
            return new BucketMatch(null, 0);
        }

        var sessionBroad = buckets.FirstOrDefault(bucket =>
            string.Equals(bucket.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(bucket.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(bucket.SessionTag, sessionTag, StringComparison.OrdinalIgnoreCase));
        if (sessionBroad is not null)
        {
            return new BucketMatch(sessionBroad, 2);
        }

        var regimeBroad = buckets.FirstOrDefault(bucket =>
            string.Equals(bucket.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(bucket.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase));
        return new BucketMatch(regimeBroad, regimeBroad is null ? 0 : 1);
    }

    private static decimal ComputeCapitalBiasMultiplier(BucketMatch backtestMatch, BucketMatch paperMatch, BucketMatch liveMatch)
    {
        decimal multiplier = 1.00m;
        
        multiplier *= ResolveSpecificityMultiplier(backtestMatch, paperMatch, liveMatch);
        multiplier *= ResolveBacktestBias(backtestMatch);
        multiplier *= ResolvePaperBias(paperMatch);
        
        if (liveMatch.Bucket is not null)
        {
            multiplier *= ResolveLiveStageBias(liveMatch.Bucket);
            multiplier *= ResolveLiveTradeCountBias(liveMatch.Bucket);
            multiplier *= ResolveLivePerformanceBias(liveMatch.Bucket);
        }

        return Math.Clamp(multiplier, 0.75m, 1.20m);
    }

    private static decimal ResolveSpecificityMultiplier(BucketMatch backtest, BucketMatch paper, BucketMatch live)
    {
        var highestSpecificity = Math.Max(live.Specificity, Math.Max(paper.Specificity, backtest.Specificity));
        return highestSpecificity switch
        {
            4 => 1.08m,
            3 => 1.04m,
            2 => 0.96m,
            1 => 0.90m,
            _ => 0.85m
        };
    }

    private static decimal ResolveBacktestBias(BucketMatch backtest)
    {
        return (backtest.Bucket?.BacktestPass == true && backtest.Bucket.ProfitFactor >= 1.20) ? 1.02m : 1.00m;
    }

    private static decimal ResolvePaperBias(BucketMatch paper)
    {
        if (paper.Bucket?.PaperPass != true) return 1.00m;
        return (paper.Bucket.ProfitFactor >= 1.25 && paper.Bucket.ExpectedValue > 0) ? 1.04m : 0.97m;
    }

    private static decimal ResolveLiveStageBias(StrategyBucketPerformanceSnapshot bucket)
    {
        return bucket.PromotionStage switch
        {
            "ScaledLive" => 1.08m,
            "LiveProbe" => 1.00m,
            "Paper" => 0.90m,
            _ => 0.85m
        };
    }

    private static decimal ResolveLiveTradeCountBias(StrategyBucketPerformanceSnapshot bucket)
    {
        return bucket.Trades switch
        {
            >= 12 => 1.08m,
            >= 8 => 1.04m,
            >= 4 => 1.00m,
            _ => 0.90m
        };
    }

    private static decimal ResolveLivePerformanceBias(StrategyBucketPerformanceSnapshot bucket)
    {
        if (bucket.HighEdgeTrades >= 3)
        {
            if (bucket.HighEdgeProfitFactor >= 1.35 && bucket.HighEdgeWinRate >= 55)
            {
                return 1.06m;
            }

            if (bucket.HighEdgeProfitFactor < 1.10 || bucket.HighEdgeWinRate < 48)
            {
                return 0.88m;
            }

            return 0.97m;
        }

        return (bucket.ProfitFactor < 1.05 || bucket.WinRate < 45) ? 0.90m : 1.00m;
    }

    private static decimal ApplyCapitalBiasToStage(decimal baseMultiplier, string stage, decimal capitalBiasMultiplier)
    {
        var adjusted = baseMultiplier * capitalBiasMultiplier;
        return stage switch
        {
            "LiveProbe" => Math.Clamp(adjusted, 0.10m, 0.25m),
            "ScaledLive" => Math.Clamp(adjusted, 0.75m, 1.15m),
            _ => Math.Clamp(adjusted, 0m, 1.00m)
        };
    }

    private static string BuildCapitalBiasLabel(BucketMatch backtestMatch, BucketMatch paperMatch, BucketMatch liveMatch, decimal capitalBiasMultiplier)
    {
        return $"Capital bias {capitalBiasMultiplier:F2}x from {BuildCapitalBiasScope(backtestMatch, paperMatch, liveMatch)}.";
    }

    private static string BuildCapitalBiasScope(BucketMatch backtestMatch, BucketMatch paperMatch, BucketMatch liveMatch)
    {
        var strongest = Math.Max(liveMatch.Specificity, Math.Max(paperMatch.Specificity, backtestMatch.Specificity));
        return strongest switch
        {
            4 => "exact symbol-session bucket",
            3 => "symbol bucket",
            2 => "session bucket",
            1 => "broad strategy bucket",
            _ => "unproven bucket"
        };
    }

    private FinalAllocatorBiasPromotionGuard? ComputeVenuePromotionBias(Signal signal, BucketMatch liveMatch)
    {
        var live = liveMatch.Bucket;
        if (live is null)
        {
            return null;
        }

        var profile = _executionProfiles.ResolveForSymbol(signal.Symbol, signal.Metadata);
        var venueLabel = $"{profile.Venue} {profile.Market}".Trim();

        if (live.BoostedVenueExecutionTrades >= 3)
        {
            if (live.BoostedVenueExecutionExpectedValue <= 0m ||
                live.BoostedVenueExecutionProfitFactor < 0.95 ||
                live.BoostedVenueExecutionWinRate < 42)
            {
                return new FinalAllocatorBiasPromotionGuard(
                    false,
                    false,
                    0m,
                    $"{venueLabel} venue learning blocked promotion: boosted execution outcomes are underperforming (PF {live.BoostedVenueExecutionProfitFactor:F2}, WR {live.BoostedVenueExecutionWinRate:F1}%, EV {live.BoostedVenueExecutionExpectedValue:F2}).");
            }

            if (live.BoostedVenueExecutionProfitFactor < 1.15 ||
                live.BoostedVenueExecutionWinRate < 50 ||
                live.BoostedVenueExecutionExpectedValue <= 0m)
            {
                return new FinalAllocatorBiasPromotionGuard(
                    true,
                    true,
                    0.20m,
                    $"{venueLabel} venue learning is still soft: boosted execution outcomes are not strong enough for scaled deployment (PF {live.BoostedVenueExecutionProfitFactor:F2}, WR {live.BoostedVenueExecutionWinRate:F1}%, EV {live.BoostedVenueExecutionExpectedValue:F2}).");
            }
        }

        if (live.TrimmedVenueExecutionTrades >= 3 &&
            live.TrimmedVenueExecutionExpectedValue > 0m &&
            live.TrimmedVenueExecutionProfitFactor >= 1.10 &&
            live.TrimmedVenueExecutionWinRate >= 48)
        {
            return new FinalAllocatorBiasPromotionGuard(
                true,
                true,
                0.18m,
                $"{venueLabel} venue learning stays cautious: trimmed execution outcomes are still outperforming, so promotion remains probe-sized.");
        }

        return null;
    }

    private FinalAllocatorBiasPromotionGuard? ComputeExactBucketPromotionBias(Signal signal, TradeDecision decision, BucketMatch liveMatch)
    {
        var live = liveMatch.Bucket;
        if (live is null || liveMatch.Specificity < 4 || live.Trades < 4)
        {
            return null;
        }

        if (live.ExpectedValue <= 0m || live.ProfitFactor < 0.95 || live.WinRate < 42)
        {
            return new FinalAllocatorBiasPromotionGuard(
                false,
                false,
                0m,
                $"Exact bucket learning blocked promotion for {signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag} in {live.SessionTag}: PF {live.ProfitFactor:F2}, WR {live.WinRate:F1}%, EV {live.ExpectedValue:F2}.");
        }

        if (live.HighEdgeTrades >= 3 &&
            (live.HighEdgeExpectedValue <= 0m || live.HighEdgeProfitFactor < 1.05 || live.HighEdgeWinRate < 46))
        {
            return new FinalAllocatorBiasPromotionGuard(
                false,
                false,
                0m,
                $"Exact bucket learning blocked promotion for {signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag}: high-edge exact outcomes are failing (PF {live.HighEdgeProfitFactor:F2}, WR {live.HighEdgeWinRate:F1}%, EV {live.HighEdgeExpectedValue:F2}).");
        }

        if (live.ProfitFactor < 1.12 || live.WinRate < 49 || live.ExpectedValue <= 0m)
        {
            return new FinalAllocatorBiasPromotionGuard(
                true,
                true,
                0.22m,
                $"Exact bucket learning stays cautious for {signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag}: exact bucket is still soft (PF {live.ProfitFactor:F2}, WR {live.WinRate:F1}%, EV {live.ExpectedValue:F2}).");
        }

        return null;
    }

    private static FinalAllocatorBiasPromotionGuard? ComputeExactVenueConcordancePromotionBias(
        Signal signal,
        TradeDecision decision,
        FinalAllocatorBiasResult exactBucketSizingBias,
        FinalAllocatorBiasResult venueExecutionBias)
    {
        var exactWeak = exactBucketSizingBias.SizeMultiplier <= 0.94m;
        var venueWeak = venueExecutionBias.SizeMultiplier <= 0.92m;
        var exactStrong = exactBucketSizingBias.SizeMultiplier >= 1.03m;
        var venueStrong = venueExecutionBias.SizeMultiplier >= 1.02m;

        if (exactWeak && venueWeak)
        {
            return new FinalAllocatorBiasPromotionGuard(
                false,
                false,
                0m,
                $"Concordance blocked promotion for {signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag}: exact-bucket edge and venue execution are both soft.");
        }

        if ((exactWeak && venueExecutionBias.SizeMultiplier < 1.0m) ||
            (venueWeak && exactBucketSizingBias.SizeMultiplier < 1.0m))
        {
            return new FinalAllocatorBiasPromotionGuard(
                true,
                true,
                0.18m,
                $"Concordance stays cautious for {signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag}: exact-bucket edge and venue execution are leaning the same defensive way.");
        }

        if (exactStrong && venueStrong)
        {
            return new FinalAllocatorBiasPromotionGuard(
                true,
                false,
                1.04m,
                $"Concordance supports scaling for {signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag}: exact-bucket edge and venue execution are both healthy.");
        }

        return null;
    }

    private FinalAllocatorBiasResult ComputeFinalExitPolicyBias(
        string symbol,
        string strategyTag,
        string regimeTag,
        string sessionTag,
        string exitPolicyTag)
    {
        var policy = string.IsNullOrWhiteSpace(exitPolicyTag) ? "TrendRunner" : exitPolicyTag.Trim();
        var liveMatch = FindBestExitPolicyMatch(symbol, strategyTag, regimeTag, sessionTag, policy, "REAL");
        if (liveMatch.Snapshot is null || liveMatch.Snapshot.Trades < 3)
        {
            return new FinalAllocatorBiasResult(1.00m, string.Empty, "no live exit sample");
        }

        var drift = EvaluateExitPolicyDrift(symbol, strategyTag, regimeTag, sessionTag, policy);
        var live = liveMatch.Snapshot;
        var scope = ResolveExitPolicyScope(liveMatch.Specificity);

        if (drift is not null && drift.Value.Score >= 0.65)
            return new FinalAllocatorBiasResult(0.88m, $"{policy} is drifting live vs paper ({drift.Value.Score:F2}) in {scope}.", scope);

        if (live.ExpectedValue <= 0m || live.ProfitFactor < 0.95 || live.WinRate < 40)
            return new FinalAllocatorBiasResult(0.92m, $"{policy} is underperforming live in {scope} (PF {live.ProfitFactor:F2}, WR {live.WinRate:F1}%, EV {live.ExpectedValue:F2}).", scope);

        if (drift is not null && drift.Value.Score >= 0.45)
            return new FinalAllocatorBiasResult(0.96m, $"{policy} has elevated live drift ({drift.Value.Score:F2}) in {scope}.", scope);

        return ResolveLivePerformanceTier(live, liveMatch.Specificity, policy, scope);
    }

    private static string ResolveExitPolicyScope(int specificity)
    {
        return specificity switch
        {
            4 => "exact exit bucket",
            3 => "symbol exit bucket",
            2 => "session exit bucket",
            1 => "broad exit bucket",
            _ => "unproven exit bucket"
        };
    }

    private static FinalAllocatorBiasResult ResolveLivePerformanceTier(ExitPolicyPerformanceSnapshot live, int specificity, string policy, string scope)
    {
        if (live.ExpectedValue > 0m && live.ProfitFactor >= 1.40 && live.WinRate >= 58)
        {
            return new FinalAllocatorBiasResult(
                specificity >= 3 ? 1.08m : 1.05m,
                $"{policy} is winning live in {scope} (PF {live.ProfitFactor:F2}, WR {live.WinRate:F1}%, EV {live.ExpectedValue:F2}).",
                scope);
        }

        if (live.ExpectedValue > 0m && live.ProfitFactor >= 1.20 && live.WinRate >= 50)
        {
            return new FinalAllocatorBiasResult(
                specificity >= 3 ? 1.04m : 1.02m,
                $"{policy} is healthy live in {scope} (PF {live.ProfitFactor:F2}, WR {live.WinRate:F1}%, EV {live.ExpectedValue:F2}).",
                scope);
        }

        return new FinalAllocatorBiasResult(1.00m, string.Empty, scope);
    }

    private FinalAllocatorBiasResult ComputeExactBucketSizingBias(Signal signal, TradeDecision decision, BucketMatch liveMatch)
    {
        var live = liveMatch.Bucket;
        if (live is null || liveMatch.Specificity < 4 || live.Trades < 4)
        {
            return new FinalAllocatorBiasResult(1.00m, string.Empty, "no exact bucket");
        }

        if (live.HighEdgeTrades >= 4 &&
            live.HighEdgeExpectedValue > 0m &&
            live.HighEdgeProfitFactor >= 1.35 &&
            live.HighEdgeWinRate >= 56)
        {
            return new FinalAllocatorBiasResult(
                1.07m,
                $"{signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag} is winning in the exact symbol-session bucket (PF {live.HighEdgeProfitFactor:F2}, WR {live.HighEdgeWinRate:F1}%, EV {live.HighEdgeExpectedValue:F2}).",
                "exact symbol-session bucket");
        }

        if (live.ExpectedValue > 0m &&
            live.ProfitFactor >= 1.22 &&
            live.WinRate >= 52)
        {
            return new FinalAllocatorBiasResult(
                1.03m,
                $"{signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag} is healthy in the exact symbol-session bucket (PF {live.ProfitFactor:F2}, WR {live.WinRate:F1}%, EV {live.ExpectedValue:F2}).",
                "exact symbol-session bucket");
        }

        if (live.HighEdgeTrades >= 3 &&
            (live.HighEdgeExpectedValue <= 0m || live.HighEdgeProfitFactor < 1.05 || live.HighEdgeWinRate < 46))
        {
            return new FinalAllocatorBiasResult(
                0.90m,
                $"{signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag} is failing in the exact symbol-session bucket (PF {live.HighEdgeProfitFactor:F2}, WR {live.HighEdgeWinRate:F1}%, EV {live.HighEdgeExpectedValue:F2}).",
                "exact symbol-session bucket");
        }

        if (live.ExpectedValue <= 0m || live.ProfitFactor < 1.00 || live.WinRate < 45)
        {
            return new FinalAllocatorBiasResult(
                0.94m,
                $"{signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag} is soft in the exact symbol-session bucket (PF {live.ProfitFactor:F2}, WR {live.WinRate:F1}%, EV {live.ExpectedValue:F2}).",
                "exact symbol-session bucket");
        }

        return new FinalAllocatorBiasResult(1.00m, string.Empty, "exact symbol-session bucket");
    }

    private static FinalAllocatorBiasResult ComputeAllocatorLearningBias(BucketMatch liveMatch)
    {
        var live = liveMatch.Bucket;
        if (live is null)
        {
            return new FinalAllocatorBiasResult(1.00m, string.Empty, "no live bucket");
        }

        if (live.BoostedAllocatorTrades >= 3)
        {
            if (live.BoostedAllocatorExpectedValue > 0m && live.BoostedAllocatorProfitFactor >= 1.15 && live.BoostedAllocatorWinRate >= 50)
            {
                return new FinalAllocatorBiasResult(
                    1.03m,
                    $"boosted allocations are working (PF {live.BoostedAllocatorProfitFactor:F2}, WR {live.BoostedAllocatorWinRate:F1}%, EV {live.BoostedAllocatorExpectedValue:F2})",
                    "boosted allocator memory");
            }

            if (live.BoostedAllocatorExpectedValue <= 0m || live.BoostedAllocatorProfitFactor < 0.95 || live.BoostedAllocatorWinRate < 42)
            {
                return new FinalAllocatorBiasResult(
                    0.92m,
                    $"boosted allocations are underperforming (PF {live.BoostedAllocatorProfitFactor:F2}, WR {live.BoostedAllocatorWinRate:F1}%, EV {live.BoostedAllocatorExpectedValue:F2})",
                    "boosted allocator memory");
            }
        }

        if (live.TrimmedAllocatorTrades >= 3 &&
            live.TrimmedAllocatorExpectedValue > 0m &&
            live.TrimmedAllocatorProfitFactor >= 1.10 &&
            live.TrimmedAllocatorWinRate >= 48)
        {
            return new FinalAllocatorBiasResult(
                0.97m,
                $"trimmed allocations are still outperforming, so size stays disciplined (PF {live.TrimmedAllocatorProfitFactor:F2}, WR {live.TrimmedAllocatorWinRate:F1}%, EV {live.TrimmedAllocatorExpectedValue:F2})",
                "trimmed allocator memory");
        }

        return new FinalAllocatorBiasResult(1.00m, string.Empty, "allocator memory neutral");
    }

    private FinalAllocatorBiasResult ComputeVenueExecutionBias(Signal signal, BucketMatch liveMatch)
    {
        var profile = _executionProfiles.ResolveForSymbol(signal.Symbol, signal.Metadata);
        var live = liveMatch.Bucket;
        var venueLabel = $"{profile.Venue} {profile.Market}".Trim();
        var roundTripCostPercent = profile.SlippagePercent + profile.SpreadPercent + (profile.CommissionPercentPerSide * 2m);
        var learningBias = ComputeVenueExecutionLearningBias(liveMatch, venueLabel);

        if (live is null || live.Trades < 3)
        {
            if (roundTripCostPercent <= 0.07m)
            {
                var baseMultiplier = 1.01m * learningBias.SizeMultiplier;
                return new FinalAllocatorBiasResult(
                    Math.Clamp(baseMultiplier, 0.90m, 1.08m),
                    $"{venueLabel} baseline execution cost is lean ({roundTripCostPercent:F3}% round-trip).",
                    "venue baseline");
            }

            if (roundTripCostPercent >= 0.18m)
            {
                var baseMultiplier = 0.98m * learningBias.SizeMultiplier;
                return new FinalAllocatorBiasResult(
                    Math.Clamp(baseMultiplier, 0.90m, 1.08m),
                    $"{venueLabel} baseline execution cost is heavy ({roundTripCostPercent:F3}% round-trip).",
                    "venue baseline");
            }

            return new FinalAllocatorBiasResult(1.00m, string.Empty, "venue baseline neutral");
        }

        var slippageRatio = live.AverageEntrySlippagePercent / Math.Max(0.01m, profile.SlippagePercent);
        var latencyRatio = live.AverageExecutionLatencyMs / Math.Max(1d, profile.PreferredMaxLatencyMs);

        if (slippageRatio >= 1.80m || latencyRatio >= 1.75d)
        {
            var baseMultiplier = 0.86m * learningBias.SizeMultiplier;
            return new FinalAllocatorBiasResult(
                Math.Clamp(baseMultiplier, 0.82m, 1.08m),
                $"{venueLabel} execution is degraded live (slip {live.AverageEntrySlippagePercent:F3}% vs {profile.SlippagePercent:F3}%, latency {live.AverageExecutionLatencyMs:F0}ms vs {profile.PreferredMaxLatencyMs}ms).{AppendLearningReason(learningBias)}",
                "venue execution");
        }

        if (slippageRatio >= 1.25m || latencyRatio >= 1.20d)
        {
            var baseMultiplier = 0.92m * learningBias.SizeMultiplier;
            return new FinalAllocatorBiasResult(
                Math.Clamp(baseMultiplier, 0.82m, 1.08m),
                $"{venueLabel} execution is softer than target (slip {live.AverageEntrySlippagePercent:F3}% vs {profile.SlippagePercent:F3}%, latency {live.AverageExecutionLatencyMs:F0}ms vs {profile.PreferredMaxLatencyMs}ms).{AppendLearningReason(learningBias)}",
                "venue execution");
        }

        if (slippageRatio <= 0.80m && latencyRatio <= 0.75d)
        {
            var baseMultiplier = (liveMatch.Specificity >= 3 ? 1.03m : 1.01m) * learningBias.SizeMultiplier;
            return new FinalAllocatorBiasResult(
                Math.Clamp(baseMultiplier, 0.90m, 1.08m),
                $"{venueLabel} execution is clean live (slip {live.AverageEntrySlippagePercent:F3}% vs {profile.SlippagePercent:F3}%, latency {live.AverageExecutionLatencyMs:F0}ms vs {profile.PreferredMaxLatencyMs}ms).{AppendLearningReason(learningBias)}",
                "venue execution");
        }

        if (learningBias.SizeMultiplier != 1.0m)
        {
            return new FinalAllocatorBiasResult(
                Math.Clamp(1.00m * learningBias.SizeMultiplier, 0.90m, 1.08m),
                $"{venueLabel} execution is neutral against baseline.{AppendLearningReason(learningBias)}",
                "venue execution");
        }

        return new FinalAllocatorBiasResult(1.00m, string.Empty, "venue execution neutral");
    }

    private static FinalAllocatorBiasResult ComputeExactVenueConcordanceSizingBias(
        Signal signal,
        TradeDecision decision,
        FinalAllocatorBiasResult exactBucketSizingBias,
        FinalAllocatorBiasResult venueExecutionBias)
    {
        var exactWeak = exactBucketSizingBias.SizeMultiplier <= 0.94m;
        var venueWeak = venueExecutionBias.SizeMultiplier <= 0.92m;
        var exactStrong = exactBucketSizingBias.SizeMultiplier >= 1.03m;
        var venueStrong = venueExecutionBias.SizeMultiplier >= 1.02m;

        if (exactStrong && venueStrong)
        {
            return new FinalAllocatorBiasResult(
                1.03m,
                $"{signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag} has aligned exact-bucket edge and venue execution support.",
                "exact-bucket/venue concordance");
        }

        if (exactWeak && venueWeak)
        {
            return new FinalAllocatorBiasResult(
                0.93m,
                $"{signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag} is being squeezed by both weak exact-bucket edge and weak venue execution.",
                "exact-bucket/venue concordance");
        }

        if (exactStrong && venueWeak)
        {
            return new FinalAllocatorBiasResult(
                0.96m,
                $"{signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag} has real exact-bucket edge, but venue execution is poisoning it live.",
                "exact-bucket/venue concordance");
        }

        if (exactWeak && venueStrong)
        {
            return new FinalAllocatorBiasResult(
                0.97m,
                $"{signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag} has clean venue execution, but the exact bucket is not proving enough edge yet.",
                "exact-bucket/venue concordance");
        }

        return new FinalAllocatorBiasResult(1.00m, string.Empty, "exact-bucket/venue concordance neutral");
    }

    private static FinalAllocatorBiasResult ComputeVenueExecutionLearningBias(BucketMatch liveMatch, string venueLabel)
    {
        var live = liveMatch.Bucket;
        if (live is null)
        {
            return new FinalAllocatorBiasResult(1.00m, string.Empty, "no venue memory");
        }

        if (live.BoostedVenueExecutionTrades >= 3)
        {
            if (live.BoostedVenueExecutionExpectedValue > 0m &&
                live.BoostedVenueExecutionProfitFactor >= 1.15 &&
                live.BoostedVenueExecutionWinRate >= 50)
            {
                return new FinalAllocatorBiasResult(
                    1.02m,
                    $"{venueLabel} boosted execution outcomes are working (PF {live.BoostedVenueExecutionProfitFactor:F2}, WR {live.BoostedVenueExecutionWinRate:F1}%, EV {live.BoostedVenueExecutionExpectedValue:F2})",
                    "venue learning");
            }

            if (live.BoostedVenueExecutionExpectedValue <= 0m ||
                live.BoostedVenueExecutionProfitFactor < 0.95 ||
                live.BoostedVenueExecutionWinRate < 42)
            {
                return new FinalAllocatorBiasResult(
                    0.88m,
                    $"{venueLabel} boosted execution outcomes are underperforming (PF {live.BoostedVenueExecutionProfitFactor:F2}, WR {live.BoostedVenueExecutionWinRate:F1}%, EV {live.BoostedVenueExecutionExpectedValue:F2})",
                    "venue learning");
            }
        }

        if (live.TrimmedVenueExecutionTrades >= 3 &&
            live.TrimmedVenueExecutionExpectedValue > 0m &&
            live.TrimmedVenueExecutionProfitFactor >= 1.10 &&
            live.TrimmedVenueExecutionWinRate >= 48)
        {
            return new FinalAllocatorBiasResult(
                0.98m,
                $"{venueLabel} trimmed execution outcomes still outperform, so venue sizing stays disciplined (PF {live.TrimmedVenueExecutionProfitFactor:F2}, WR {live.TrimmedVenueExecutionWinRate:F1}%, EV {live.TrimmedVenueExecutionExpectedValue:F2})",
                "venue learning");
        }

        return new FinalAllocatorBiasResult(1.00m, string.Empty, "venue learning neutral");
    }

    private static string AppendLearningReason(FinalAllocatorBiasResult learningBias)
        => learningBias.SizeMultiplier == 1.0m || string.IsNullOrWhiteSpace(learningBias.Reason)
            ? string.Empty
            : $" Venue learning: {learningBias.Reason}";

    private static string BuildConcordanceSuffix(FinalAllocatorBiasPromotionGuard? concordanceBias)
        => concordanceBias.HasValue && !string.IsNullOrWhiteSpace(concordanceBias.Value.Reason)
            ? $" {concordanceBias.Value.Reason}"
            : string.Empty;

    private ExitPolicyDriftSnapshot? EvaluateExitPolicyDrift(
        string symbol,
        string strategyTag,
        string regimeTag,
        string sessionTag,
        string exitPolicyTag)
    {
        var policy = string.IsNullOrWhiteSpace(exitPolicyTag) ? "TrendRunner" : exitPolicyTag.Trim();
        var paperMatch = FindBestExitPolicyMatch(symbol, strategyTag, regimeTag, sessionTag, policy, "PAPER");
        var liveMatch = FindBestExitPolicyMatch(symbol, strategyTag, regimeTag, sessionTag, policy, "REAL");
        if (paperMatch.Snapshot is null || liveMatch.Snapshot is null)
        {
            return null;
        }

        if (paperMatch.Snapshot.Trades < 2 || liveMatch.Snapshot.Trades < 2)
        {
            return null;
        }

        var score = ComputeExitPolicyDriftScore(paperMatch.Snapshot, liveMatch.Snapshot);
        return new ExitPolicyDriftSnapshot(
            score,
            paperMatch.Snapshot.WinRate,
            liveMatch.Snapshot.WinRate,
            paperMatch.Snapshot.ProfitFactor,
            liveMatch.Snapshot.ProfitFactor,
            paperMatch.Snapshot.ExpectedValue,
            liveMatch.Snapshot.ExpectedValue);
    }

    private BucketExitPolicyMatch FindBestExitPolicyMatch(
        string symbol,
        string strategyTag,
        string regimeTag,
        string sessionTag,
        string exitPolicyTag,
        string executionMode)
    {
        var exact = _state.ExitPolicyBuckets.FirstOrDefault(snapshot =>
            string.Equals(snapshot.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.SessionTag, sessionTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.ExitPolicyTag, exitPolicyTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.ExecutionMode, executionMode, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return new BucketExitPolicyMatch(exact, 4);
        }

        var symbolBroad = _state.ExitPolicyBuckets.FirstOrDefault(snapshot =>
            string.Equals(snapshot.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.ExitPolicyTag, exitPolicyTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.ExecutionMode, executionMode, StringComparison.OrdinalIgnoreCase));
        if (symbolBroad is not null)
        {
            return new BucketExitPolicyMatch(symbolBroad, 3);
        }

        var sessionBroad = _state.ExitPolicyBuckets.FirstOrDefault(snapshot =>
            string.Equals(snapshot.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.SessionTag, sessionTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.ExitPolicyTag, exitPolicyTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.ExecutionMode, executionMode, StringComparison.OrdinalIgnoreCase));
        if (sessionBroad is not null)
        {
            return new BucketExitPolicyMatch(sessionBroad, 2);
        }

        var regimeBroad = _state.ExitPolicyBuckets.FirstOrDefault(snapshot =>
            string.Equals(snapshot.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.ExitPolicyTag, exitPolicyTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.ExecutionMode, executionMode, StringComparison.OrdinalIgnoreCase));
        return new BucketExitPolicyMatch(regimeBroad, regimeBroad is null ? 0 : 1);
    }

    private static double ComputeExitPolicyDriftScore(ExitPolicyPerformanceSnapshot paper, ExitPolicyPerformanceSnapshot live)
    {
        var winRateDrift = Math.Abs(paper.WinRate - live.WinRate) / 100.0;
        var profitFactorBase = Math.Max(0.5, paper.ProfitFactor);
        var profitFactorDrift = Math.Abs(paper.ProfitFactor - live.ProfitFactor) / profitFactorBase;
        var expectedValueBase = Math.Max(1m, Math.Abs(paper.ExpectedValue));
        var expectedValueDrift = (double)(Math.Abs(paper.ExpectedValue - live.ExpectedValue) / expectedValueBase);
        return Math.Min(1.50, (winRateDrift * 0.35) + (profitFactorDrift * 0.35) + (expectedValueDrift * 0.30));
    }

    private static double ComputeDriftScore(StrategyBucketPerformanceSnapshot paper, StrategyBucketPerformanceSnapshot live)
    {
        var winRateDrift = Math.Abs(paper.WinRate - live.WinRate) / 100.0;
        var profitFactorBase = Math.Max(0.5, paper.ProfitFactor);
        var profitFactorDrift = Math.Abs(paper.ProfitFactor - live.ProfitFactor) / profitFactorBase;
        var expectedValueBase = Math.Max(1m, Math.Abs(paper.ExpectedValue));
        var expectedValueDrift = (double)(Math.Abs(paper.ExpectedValue - live.ExpectedValue) / expectedValueBase);
        var slippageBase = Math.Max(0.05m, paper.AverageEntrySlippagePercent + 0.05m);
        var slippageDrift = (double)(Math.Abs(paper.AverageEntrySlippagePercent - live.AverageEntrySlippagePercent) / slippageBase);
        var latencyBase = Math.Max(25d, paper.AverageExecutionLatencyMs + 25d);
        var latencyDrift = Math.Abs(paper.AverageExecutionLatencyMs - live.AverageExecutionLatencyMs) / latencyBase;

        return Math.Min(1.5, (winRateDrift * 0.25) + (profitFactorDrift * 0.20) + (expectedValueDrift * 0.20) + (slippageDrift * 0.20) + (latencyDrift * 0.15));
    }

    private static decimal GetLivePnl(Trade trade)
    {
        var direction = trade.Side == TradeSide.Sell ? -1m : 1m;
        var grossPnl = (trade.ExitPrice - trade.EntryPrice) * trade.Quantity * direction;
        var feeRate = 0.0005m; // 5 bps per side, 10 bps round trip
        var fees = (trade.EntryPrice * trade.Quantity + trade.ExitPrice * trade.Quantity) * feeRate;
        var slippageCost = trade.EntryPrice * trade.Quantity * (trade.EntrySlippagePercent / 100m);
        return grossPnl - fees - slippageCost;
    }

    private static Trade MapToTrade(BacktestTradeResult br) => new Trade
    {
        Symbol = "", // Will be grouped by result.Symbol
        Side = br.Side,
        EntryPrice = br.EntryPrice,
        ExitPrice = br.ExitPrice,
        Quantity = 1.0m, // Quantity isn't always in backtest result, default to 1 for PnL per unit
        StrategyTag = br.StrategyTag,
        RegimeTag = br.RegimeTag,
        ExitPolicyTag = br.ExitPolicyTag,
        ExecutionMode = br.ExecutionMode,
        PromotionStage = br.PromotionStage,
        EntryTime = br.EntryTime,
        ExitTime = br.ExitTime,
        TotalFeesPaid = br.TotalFeesPaid,
        NetFunding = br.NetFunding
    };

    private static decimal GetPaperPnl(PaperPosition position)
    {
        var feeRate = 0.0005m; // 5 bps per side
        var exitPrice = position.CurrentPrice > 0 ? position.CurrentPrice : position.EntryPrice;
        var fees = (position.EntryPrice * position.Quantity + exitPrice * position.Quantity) * feeRate;
        return position.RealizedPnL - fees;
    }

    private static bool IsHighEdgeBand(string? edgeBand, decimal edgeScore)
    {
        if (string.Equals(edgeBand, "Full", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(edgeBand, "Reduced", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return edgeScore >= 70m;
    }

    private static string DetermineLivePromotionStage(int liveTrades, bool scaledLiveEligible, bool liveProbePass)
    {
        if (liveTrades < 4 || !liveProbePass)
        {
            return "LiveProbe";
        }

        return scaledLiveEligible ? "ScaledLive" : "Paper";
    }

    private void RefreshPortfolioValidationState()
    {
        var sessions = _state.BacktestBuckets
            .Select(snapshot => new BacktestSessionResult
            {
                Symbol = snapshot.Symbol,
                Version = snapshot.BacktestVersion,
                BacktestPass = snapshot.BacktestPass,
                TotalTrades = snapshot.Trades,
                TotalPnL = snapshot.TotalPnL,
                SharpeRatio = snapshot.SharpeRatio,
                ProfitFactor = snapshot.ProfitFactor,
                ExpectancyPerTrade = snapshot.ExpectedValue
            })
            .ToList();

        var validation = BacktestOrchestrator.EvaluatePortfolioValidation(sessions, _qualityGate);
        _state.PortfolioValidationReady = validation.IsReady;
        _state.PortfolioReadySymbols = validation.PassedSymbols;
        _state.PortfolioRequiredSymbols = _qualityGate.MinPortfolioReadySymbols;
        _state.PortfolioPooledPnL = validation.PooledPnL;
        _state.PortfolioAverageSharpe = validation.AverageSharpe;
        _state.PortfolioAverageProfitFactor = validation.AverageProfitFactor;
        _state.PortfolioAverageExpectancyPerTrade = validation.AverageExpectancyPerTrade;
        _state.PortfolioValidationSummary =
            $"{validation.PassedSymbols}/{validation.TotalSymbols} symbols cleared the backtest gate. " +
            $"Need {_qualityGate.MinPortfolioReadySymbols}, pooled PnL {validation.PooledPnL:N2}, avg Sharpe {validation.AverageSharpe:F2}, avg PF {validation.AverageProfitFactor:F2}, avg expectancy {validation.AverageExpectancyPerTrade:N2}.";
        _state.PortfolioValidationRecordedAtUtc = DateTime.UtcNow;
    }

    private static decimal ComputeMaxDrawdownPercent(IReadOnlyList<decimal> equityCurve)
    {
        if (equityCurve.Count == 0)
        {
            return 0m;
        }

        var peak = equityCurve[0];
        var maxDrawdown = 0m;
        foreach (var equity in equityCurve)
        {
            if (equity > peak)
            {
                peak = equity;
            }

            if (peak <= 0m)
            {
                continue;
            }

            var drawdown = ((peak - equity) / peak) * 100m;
            if (drawdown > maxDrawdown)
            {
                maxDrawdown = drawdown;
            }
        }

        return maxDrawdown;
    }

    private static double ComputeSharpeRatio(IReadOnlyList<decimal> percentReturns)
    {
        if (percentReturns.Count < 2)
        {
            return 0d;
        }

        var returns = percentReturns.Select(value => (double)value).ToList();
        var mean = returns.Average();
        var variance = returns.Sum(value => Math.Pow(value - mean, 2)) / (returns.Count - 1);
        var stdDev = Math.Sqrt(Math.Max(variance, 0d));
        return stdDev > 0d ? mean / stdDev * Math.Sqrt(252) : 0d;
    }

    private static BacktestQualityGateOptions BuildQualityGate(IConfiguration? configuration)
    {
        var options = new BacktestQualityGateOptions
        {
            MinTrades = 6,
            MinProfitFactor = 1.15,
            MinWinRatePercent = 42.0,
            MinExpectancyPerTrade = 0.01m,
            MaxDrawdownPercent = 25m,
            MinPortfolioReadySymbols = 4,
            ShortMinSharpe = 0.5,
            LongMinSharpe = 0.5
        };
        if (configuration == null)
        {
            return options;
        }

        if (int.TryParse(configuration["Backtest:QualityGate:MinTrades"], out var minTrades)) options.MinTrades = minTrades;
        if (double.TryParse(configuration["Backtest:QualityGate:MinProfitFactor"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var minPf)) options.MinProfitFactor = minPf;
        if (double.TryParse(configuration["Backtest:QualityGate:MinWinRatePercent"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var minWin)) options.MinWinRatePercent = minWin;
        if (decimal.TryParse(configuration["Backtest:QualityGate:MinExpectancyPerTrade"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var minExp)) options.MinExpectancyPerTrade = minExp;
        if (decimal.TryParse(configuration["Backtest:QualityGate:MaxDrawdownPercent"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var maxDd)) options.MaxDrawdownPercent = maxDd;
        if (int.TryParse(configuration["Backtest:QualityGate:MinPortfolioReadySymbols"], out var minSymbols)) options.MinPortfolioReadySymbols = minSymbols;
        if (double.TryParse(configuration["Backtest:QualityGate:ShortMinSharpe"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var shortSharpe)) options.ShortMinSharpe = shortSharpe;
        if (double.TryParse(configuration["Backtest:QualityGate:LongMinSharpe"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var longSharpe)) options.LongMinSharpe = longSharpe;
        return options;
    }

    private static StrategyPromotionOptions BuildPromotionOptions(IConfiguration? configuration)
    {
        var opts = new StrategyPromotionOptions();
        if (configuration == null) return opts;

        if (bool.TryParse(configuration["Strategy:Promotion:StrictSymbolIsolation"], out var strict))
            opts.StrictSymbolIsolation = strict;
        if (decimal.TryParse(configuration["Strategy:Promotion:CrossSymbolFallbackPenalty"],
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var penalty))
            opts.CrossSymbolFallbackPenalty = penalty;
        return opts;
    }


    private readonly record struct BucketMatch(StrategyBucketPerformanceSnapshot? Bucket, int Specificity);
    private readonly record struct BucketExitPolicyMatch(ExitPolicyPerformanceSnapshot? Snapshot, int Specificity);
    private readonly record struct FinalAllocatorBiasPromotionGuard(bool Approved, bool ForceLiveProbe, decimal StageMultiplier, string Reason);
    private readonly record struct ExitPolicyDriftSnapshot(
        double Score,
        double PaperWinRate,
        double LiveWinRate,
        double PaperProfitFactor,
        double LiveProfitFactor,
        decimal PaperExpectedValue,
        decimal LiveExpectedValue);
}
