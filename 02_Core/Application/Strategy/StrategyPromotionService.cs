using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Application.Strategy;

public sealed class StrategyPromotionService
{
    private readonly IStrategyPerformanceStore _store;
    private readonly MarketExecutionProfileService _executionProfiles;
    private readonly object _sync = new();
    private StrategyPerformanceState _state;

    public StrategyPromotionService(
        IStrategyPerformanceStore store,
        MarketExecutionProfileService? executionProfiles = null)
    {
        _store = store;
        _executionProfiles = executionProfiles ?? new MarketExecutionProfileService();
        _state = new StrategyPerformanceState();
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
            var backtestBucket = backtestMatch.Bucket;
            var paperBucket = paperMatch.Bucket;
            var liveBucket = liveMatch.Bucket;
            var executionProfile = _executionProfiles.ResolveForSymbol(signal.Symbol, signal.Metadata);
            var capitalBiasMultiplier = ComputeCapitalBiasMultiplier(backtestMatch, paperMatch, liveMatch);
            var capitalBiasLabel = BuildCapitalBiasLabel(backtestMatch, paperMatch, liveMatch, capitalBiasMultiplier);
            var exitPolicyDrift = EvaluateExitPolicyDrift(signal.Symbol, decision.StrategyTag, decision.RegimeTag, sessionTag, decision.ExitPolicyTag);
            var exactBucketSizingBias = ComputeExactBucketSizingBias(signal, decision, liveMatch);
            var exactBucketPromotionBias = ComputeExactBucketPromotionBias(signal, decision, liveMatch);
            var venueExecutionBias = ComputeVenueExecutionBias(signal, liveMatch);

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

            var concordancePromotionBias = ComputeExactVenueConcordancePromotionBias(
                signal,
                decision,
                exactBucketSizingBias,
                venueExecutionBias);
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

            var venuePromotionBias = ComputeVenuePromotionBias(signal, liveMatch);
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

                if (exactBias.ForceLiveProbe)
                {
                    return new PromotionGuardResult(
                        true,
                        ApplyCapitalBiasToStage(Math.Min(0.25m, exactBias.StageMultiplier), "LiveProbe", capitalBiasMultiplier),
                        $"{exactBias.Reason} {capitalBiasLabel}",
                        false,
                        0,
                        "LiveProbe");
                }
            }

            if (exitPolicyDrift.HasValue)
            {
                var drift = exitPolicyDrift.Value;
                if (drift.Score >= 0.70 && (drift.LiveExpectedValue <= 0m || drift.LiveProfitFactor < 0.95 || drift.LiveWinRate < 42))
                {
                    return new PromotionGuardResult(
                        false,
                        0m,
                        $"Exit-policy drift too large for {decision.ExitPolicyTag} on {signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag} in {sessionTag}. Drift={drift.Score:F2}, paper EV={drift.PaperExpectedValue:F2}, live EV={drift.LiveExpectedValue:F2}, paper PF={drift.PaperProfitFactor:F2}, live PF={drift.LiveProfitFactor:F2}.",
                        false,
                        drift.Score,
                        "Paper");
                }

                if (drift.Score >= 0.45)
                {
                    return new PromotionGuardResult(
                        true,
                        ApplyCapitalBiasToStage(0.20m, "LiveProbe", capitalBiasMultiplier),
                        $"Exit-policy drift caution for {decision.ExitPolicyTag} on {signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag} in {sessionTag}. Drift={drift.Score:F2}, paper EV={drift.PaperExpectedValue:F2}, live EV={drift.LiveExpectedValue:F2}, paper PF={drift.PaperProfitFactor:F2}, live PF={drift.LiveProfitFactor:F2}. {capitalBiasLabel}",
                        false,
                        drift.Score,
                        "LiveProbe");
                }
            }

            var driftScore = ComputeDriftScore(paperBucket, liveBucket);

            if ((driftScore >= 0.75 && liveBucket.ExpectedValue <= 0) ||
                (driftScore >= 0.60 && (liveBucket.ExpectedValue <= 0 || liveBucket.ProfitFactor < 0.75)))
            {
                return new PromotionGuardResult(
                    false,
                    0m,
                    $"Paper/live drift too large for {signal.Symbol} {decision.StrategyTag}/{decision.RegimeTag} in {sessionTag}. Drift={driftScore:F2}.",
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
        // ... (preserving logic)
        // Note: keeping lock for state modification, but calling persist after
        List<StrategyBucketPerformanceSnapshot> snapshots;
        List<ExitPolicyPerformanceSnapshot> exitPolicySnapshots;

        lock (_sync)
        {
            snapshots = result.Trades
                .GroupBy(trade => new
                {
                    result.Symbol,
                    StrategyTag = string.IsNullOrWhiteSpace(trade.StrategyTag) ? "TrendConfluence" : trade.StrategyTag,
                    RegimeTag = string.IsNullOrWhiteSpace(trade.RegimeTag) ? "Unknown" : trade.RegimeTag,
                    SessionTag = ResolveSessionBucket(trade.ExitTime)
                })
                .Select(group =>
                {
                    var pnlValues = group.Select(trade => trade.PnL).ToList();
                    var backtestPass = pnlValues.Count >= 6 &&
                                       ComputeProfitFactor(pnlValues) >= 1.15 &&
                                       pnlValues.Average() > 0 &&
                                       group.Count(trade => trade.PnL > 0) * 1.0 / Math.Max(1, group.Count()) >= 0.42;

                    return new StrategyBucketPerformanceSnapshot
                    {
                        Symbol = group.Key.Symbol,
                        StrategyTag = group.Key.StrategyTag,
                        RegimeTag = group.Key.RegimeTag,
                        SessionTag = group.Key.SessionTag,
                        Trades = pnlValues.Count,
                        WinRate = pnlValues.Count == 0 ? 0 : pnlValues.Count(value => value > 0) * 100.0 / pnlValues.Count,
                        ProfitFactor = ComputeProfitFactor(pnlValues),
                        ExpectedValue = pnlValues.Count == 0 ? 0 : pnlValues.Average(),
                        TotalPnL = pnlValues.Sum(),
                        BacktestPass = backtestPass,
                        RecordedAtUtc = DateTime.UtcNow,
                        PromotionReady = backtestPass,
                        PromotionStage = "Backtest"
                    };
                })
                .ToList();

            exitPolicySnapshots = result.Trades
                .GroupBy(trade => new
                {
                    result.Symbol,
                    StrategyTag = string.IsNullOrWhiteSpace(trade.StrategyTag) ? "TrendConfluence" : trade.StrategyTag,
                    RegimeTag = string.IsNullOrWhiteSpace(trade.RegimeTag) ? "Unknown" : trade.RegimeTag,
                    SessionTag = ResolveSessionBucket(trade.ExitTime),
                    ExitPolicyTag = string.IsNullOrWhiteSpace(trade.ExitPolicyTag) ? "TrendRunner" : trade.ExitPolicyTag,
                    ExecutionMode = string.IsNullOrWhiteSpace(trade.ExecutionMode) ? "SIMULATED" : trade.ExecutionMode
                })
                .Select(group => new ExitPolicyPerformanceSnapshot
                {
                    Symbol = group.Key.Symbol,
                    StrategyTag = group.Key.StrategyTag,
                    RegimeTag = group.Key.RegimeTag,
                    SessionTag = group.Key.SessionTag,
                    ExitPolicyTag = group.Key.ExitPolicyTag,
                    ExecutionMode = group.Key.ExecutionMode,
                    Trades = group.Count(),
                    WinRate = group.Count() == 0 ? 0 : group.Count(item => item.PnL > 0) * 100.0 / group.Count(),
                    ProfitFactor = ComputeProfitFactor(group.Select(item => item.PnL)),
                    ExpectedValue = group.Any() ? group.Average(item => item.PnL) : 0m,
                    TotalPnL = group.Sum(item => item.PnL),
                    RecordedAtUtc = DateTime.UtcNow
                })
                .ToList();

            foreach (var snapshot in snapshots)
            {
                _state.BacktestBuckets.RemoveAll(existing =>
                    string.Equals(existing.Symbol, snapshot.Symbol, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.StrategyTag, snapshot.StrategyTag, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.RegimeTag, snapshot.RegimeTag, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.SessionTag, snapshot.SessionTag, StringComparison.OrdinalIgnoreCase));
                _state.BacktestBuckets.Add(snapshot);
            }
            UpsertExitPolicySnapshots(exitPolicySnapshots);
        }

        await PersistAsync();
    }

    public async Task RecordPaperState(IEnumerable<PaperPosition> closedPositions)
    {
        List<StrategyBucketPerformanceSnapshot> paperSnapshots;
        List<ExitPolicyPerformanceSnapshot> exitPolicySnapshots;

        var closedList = closedPositions
            .Where(position => position.ExitTime.HasValue)
            .ToList();

        lock (_sync)
        {
            paperSnapshots = closedList
                .GroupBy(position => new
                {
                    position.Symbol,
                    position.StrategyTag,
                    position.RegimeTag,
                    SessionTag = ResolveSessionBucket(position.ExitTime!.Value)
                })
                .Select(group =>
                {
                    var pnlValues = group.Select(GetPaperPnl).ToList();
                    var backtestBucket = FindBestBucketMatch(_state.BacktestBuckets, group.Key.Symbol, group.Key.StrategyTag, group.Key.RegimeTag, group.Key.SessionTag).Bucket;
                    var backtestPass = backtestBucket?.BacktestPass == true;
                    var paperPass = backtestPass &&
                                    pnlValues.Count >= 8 &&
                                    ComputeProfitFactor(pnlValues) >= 1.20 &&
                                    pnlValues.Average() > 0 &&
                                    pnlValues.Count(value => value > 0) * 1.0 / pnlValues.Count >= 0.45;

                    return new StrategyBucketPerformanceSnapshot
                    {
                        Symbol = group.Key.Symbol,
                        StrategyTag = group.Key.StrategyTag,
                        RegimeTag = group.Key.RegimeTag,
                        SessionTag = group.Key.SessionTag,
                        Trades = pnlValues.Count,
                        WinRate = pnlValues.Count == 0 ? 0 : pnlValues.Count(value => value > 0) * 100.0 / pnlValues.Count,
                        ProfitFactor = ComputeProfitFactor(pnlValues),
                        ExpectedValue = pnlValues.Count == 0 ? 0 : pnlValues.Average(),
                        TotalPnL = pnlValues.Sum(),
                        BacktestPass = backtestPass,
                        PaperPass = paperPass,
                        PromotionReady = paperPass,
                        PromotionStage = paperPass ? "Paper" : "Backtest",
                        RecordedAtUtc = DateTime.UtcNow
                    };
                })
                .ToList();

            exitPolicySnapshots = closedList
                .GroupBy(position => new
                {
                    position.Symbol,
                    position.StrategyTag,
                    position.RegimeTag,
                    SessionTag = ResolveSessionBucket(position.ExitTime!.Value),
                    ExitPolicyTag = string.IsNullOrWhiteSpace(position.ExitPolicyTag) ? "TrendRunner" : position.ExitPolicyTag,
                    ExecutionMode = string.IsNullOrWhiteSpace(position.ExecutionMode) ? "PAPER" : position.ExecutionMode
                })
                .Select(group => new ExitPolicyPerformanceSnapshot
                {
                    Symbol = group.Key.Symbol,
                    StrategyTag = group.Key.StrategyTag,
                    RegimeTag = group.Key.RegimeTag,
                    SessionTag = group.Key.SessionTag,
                    ExitPolicyTag = group.Key.ExitPolicyTag,
                    ExecutionMode = group.Key.ExecutionMode,
                    Trades = group.Count(),
                    WinRate = group.Count() == 0 ? 0 : group.Count(item => item.RealizedPnL > 0) * 100.0 / group.Count(),
                    ProfitFactor = ComputeProfitFactor(group.Select(item => item.RealizedPnL)),
                    ExpectedValue = group.Any() ? group.Average(item => item.RealizedPnL) : 0m,
                    TotalPnL = group.Sum(item => item.RealizedPnL),
                    RecordedAtUtc = DateTime.UtcNow
                })
                .ToList();

            foreach (var snapshot in paperSnapshots)
            {
                _state.PaperBuckets.RemoveAll(existing =>
                    string.Equals(existing.Symbol, snapshot.Symbol, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.StrategyTag, snapshot.StrategyTag, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.RegimeTag, snapshot.RegimeTag, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.SessionTag, snapshot.SessionTag, StringComparison.OrdinalIgnoreCase));
                _state.PaperBuckets.Add(snapshot);
            }
            UpsertExitPolicySnapshots(exitPolicySnapshots);
        }

        await PersistAsync();
    }

    public StrategyPerformanceState GetState()
    {
        lock (_sync)
        {
            return _state;
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
            .Select(group =>
            {
                var pnlValues = group.Select(GetLivePnl).ToList();
                var highEdgePnls = group
                    .Where(trade => IsHighEdgeBand(trade.EdgeBand, trade.EdgeScore))
                    .Select(GetLivePnl)
                    .ToList();
                var boostedAllocatorPnls = group
                    .Where(trade => trade.FinalAllocatorBias > 1.01m)
                    .Select(GetLivePnl)
                    .ToList();
                var trimmedAllocatorPnls = group
                    .Where(trade => trade.FinalAllocatorBias > 0m && trade.FinalAllocatorBias < 0.99m)
                    .Select(GetLivePnl)
                    .ToList();
                var boostedVenuePnls = group
                    .Where(trade => trade.VenueExecutionBias > 1.01m)
                    .Select(GetLivePnl)
                    .ToList();
                var trimmedVenuePnls = group
                    .Where(trade => trade.VenueExecutionBias > 0m && trade.VenueExecutionBias < 0.99m)
                    .Select(GetLivePnl)
                    .ToList();
                var backtestBucket = FindBestBucketMatch(_state.BacktestBuckets, group.Key.Symbol, group.Key.StrategyTag, group.Key.RegimeTag, group.Key.SessionTag).Bucket;
                var paperBucket = FindBestBucketMatch(_state.PaperBuckets, group.Key.Symbol, group.Key.StrategyTag, group.Key.RegimeTag, group.Key.SessionTag).Bucket;
                var backtestPass = backtestBucket?.BacktestPass == true;
                var paperPass = paperBucket?.PaperPass == true;
                var liveProbePass = pnlValues.Count >= 4 &&
                                    backtestPass &&
                                    paperPass &&
                                    ComputeProfitFactor(pnlValues) >= 1.0 &&
                                    pnlValues.Average() >= 0 &&
                                    (highEdgePnls.Count < 3 || (ComputeProfitFactor(highEdgePnls) >= 1.0 && highEdgePnls.Average() >= 0));
                var scaledLiveEligible = pnlValues.Count >= 8 &&
                                         backtestPass &&
                                         paperPass &&
                                         ComputeProfitFactor(pnlValues) >= 1.20 &&
                                         pnlValues.Average() > 0 &&
                                         pnlValues.Count(value => value > 0) * 1.0 / pnlValues.Count >= 0.45 &&
                                         (!group.Any(trade => trade.EdgeScore > 0m) || group.Where(trade => trade.EdgeScore > 0m).Average(trade => trade.EdgeScore) >= 70m) &&
                                         (highEdgePnls.Count < 3 || (ComputeProfitFactor(highEdgePnls) >= 1.10 && highEdgePnls.Average() > 0 && highEdgePnls.Count(value => value > 0) * 1.0 / highEdgePnls.Count >= 0.45));

                return new StrategyBucketPerformanceSnapshot
                {
                    Symbol = group.Key.Symbol,
                    StrategyTag = group.Key.StrategyTag,
                    RegimeTag = group.Key.RegimeTag,
                    SessionTag = group.Key.SessionTag,
                    Trades = pnlValues.Count,
                    WinRate = pnlValues.Count == 0 ? 0 : pnlValues.Count(value => value > 0) * 100.0 / pnlValues.Count,
                    ProfitFactor = ComputeProfitFactor(pnlValues),
                    ExpectedValue = pnlValues.Count == 0 ? 0 : pnlValues.Average(),
                    TotalPnL = pnlValues.Sum(),
                    AverageEdgeScore = group.Any(trade => trade.EdgeScore > 0m) ? group.Where(trade => trade.EdgeScore > 0m).Average(trade => trade.EdgeScore) : 0m,
                    HighEdgeTrades = highEdgePnls.Count,
                    HighEdgeWinRate = highEdgePnls.Count == 0 ? 0 : highEdgePnls.Count(value => value > 0) * 100.0 / highEdgePnls.Count,
                    HighEdgeProfitFactor = ComputeProfitFactor(highEdgePnls),
                    HighEdgeExpectedValue = highEdgePnls.Count == 0 ? 0 : highEdgePnls.Average(),
                    HighEdgeTotalPnL = highEdgePnls.Sum(),
                    AverageFinalAllocatorBias = group.Any(trade => trade.FinalAllocatorBias > 0m) ? group.Where(trade => trade.FinalAllocatorBias > 0m).Average(trade => trade.FinalAllocatorBias) : 0m,
                    BoostedAllocatorTrades = boostedAllocatorPnls.Count,
                    BoostedAllocatorWinRate = boostedAllocatorPnls.Count == 0 ? 0 : boostedAllocatorPnls.Count(value => value > 0) * 100.0 / boostedAllocatorPnls.Count,
                    BoostedAllocatorProfitFactor = ComputeProfitFactor(boostedAllocatorPnls),
                    BoostedAllocatorExpectedValue = boostedAllocatorPnls.Count == 0 ? 0 : boostedAllocatorPnls.Average(),
                    TrimmedAllocatorTrades = trimmedAllocatorPnls.Count,
                    TrimmedAllocatorWinRate = trimmedAllocatorPnls.Count == 0 ? 0 : trimmedAllocatorPnls.Count(value => value > 0) * 100.0 / trimmedAllocatorPnls.Count,
                    TrimmedAllocatorProfitFactor = ComputeProfitFactor(trimmedAllocatorPnls),
                    TrimmedAllocatorExpectedValue = trimmedAllocatorPnls.Count == 0 ? 0 : trimmedAllocatorPnls.Average(),
                    AverageVenueExecutionBias = group.Any(trade => trade.VenueExecutionBias > 0m) ? group.Where(trade => trade.VenueExecutionBias > 0m).Average(trade => trade.VenueExecutionBias) : 0m,
                    BoostedVenueExecutionTrades = boostedVenuePnls.Count,
                    BoostedVenueExecutionWinRate = boostedVenuePnls.Count == 0 ? 0 : boostedVenuePnls.Count(value => value > 0) * 100.0 / boostedVenuePnls.Count,
                    BoostedVenueExecutionProfitFactor = ComputeProfitFactor(boostedVenuePnls),
                    BoostedVenueExecutionExpectedValue = boostedVenuePnls.Count == 0 ? 0 : boostedVenuePnls.Average(),
                    TrimmedVenueExecutionTrades = trimmedVenuePnls.Count,
                    TrimmedVenueExecutionWinRate = trimmedVenuePnls.Count == 0 ? 0 : trimmedVenuePnls.Count(value => value > 0) * 100.0 / trimmedVenuePnls.Count,
                    TrimmedVenueExecutionProfitFactor = ComputeProfitFactor(trimmedVenuePnls),
                    TrimmedVenueExecutionExpectedValue = trimmedVenuePnls.Count == 0 ? 0 : trimmedVenuePnls.Average(),
                    AverageEntrySlippagePercent = group.Any() ? group.Average(trade => trade.EntrySlippagePercent) : 0m,
                    AverageExecutionLatencyMs = group.Any() ? group.Average(trade => (double)trade.ExecutionLatencyMs) : 0,
                    BacktestPass = backtestPass,
                    PaperPass = paperPass,
                    LiveProbePass = liveProbePass,
                    ScaledLiveEligible = scaledLiveEligible,
                    PromotionReady = scaledLiveEligible,
                    PromotionStage = DetermineLivePromotionStage(pnlValues.Count, scaledLiveEligible, liveProbePass),
                    RecordedAtUtc = DateTime.UtcNow
                };
            })
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
                    ExitPolicyTag = string.IsNullOrWhiteSpace(trade.ExitPolicyTag) ? "TrendRunner" : trade.ExitPolicyTag,
                    ExecutionMode = string.IsNullOrWhiteSpace(trade.ExecutionMode) ? "REAL" : trade.ExecutionMode
                })
                .Select(group => new ExitPolicyPerformanceSnapshot
                {
                    Symbol = group.Key.Symbol,
                    StrategyTag = group.Key.StrategyTag,
                    RegimeTag = group.Key.RegimeTag,
                    SessionTag = group.Key.SessionTag,
                    ExitPolicyTag = group.Key.ExitPolicyTag,
                    ExecutionMode = group.Key.ExecutionMode,
                    Trades = group.Count(),
                    WinRate = group.Count() == 0 ? 0 : group.Count(item => GetLivePnl(item) > 0) * 100.0 / group.Count(),
                    ProfitFactor = ComputeProfitFactor(group.Select(GetLivePnl)),
                    ExpectedValue = group.Any() ? group.Average(GetLivePnl) : 0m,
                    TotalPnL = group.Sum(GetLivePnl),
                    RecordedAtUtc = DateTime.UtcNow
                }));
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
        _state.RecordedAtUtc = DateTime.UtcNow;
        await _store.SaveAsync(_state);
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

    private static BucketMatch FindBestBucketMatch(
        IEnumerable<StrategyBucketPerformanceSnapshot> buckets,
        string symbol,
        string strategyTag,
        string regimeTag,
        string sessionTag)
    {
        var exact = buckets.FirstOrDefault(bucket =>
            string.Equals(bucket.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(bucket.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(bucket.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(bucket.SessionTag, sessionTag, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return new BucketMatch(exact, 4);
        }

        var symbolBroad = buckets.FirstOrDefault(bucket =>
            string.Equals(bucket.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(bucket.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(bucket.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase));
        if (symbolBroad is not null)
        {
            return new BucketMatch(symbolBroad, 3);
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
        var highestSpecificity = Math.Max(liveMatch.Specificity, Math.Max(paperMatch.Specificity, backtestMatch.Specificity));
        multiplier *= highestSpecificity switch
        {
            4 => 1.08m,
            3 => 1.04m,
            2 => 0.96m,
            1 => 0.90m,
            _ => 0.85m
        };

        if (backtestMatch.Bucket?.BacktestPass == true && backtestMatch.Bucket.ProfitFactor >= 1.20)
        {
            multiplier *= 1.02m;
        }

        if (paperMatch.Bucket?.PaperPass == true)
        {
            multiplier *= paperMatch.Bucket.ProfitFactor >= 1.25 && paperMatch.Bucket.ExpectedValue > 0
                ? 1.04m
                : 0.97m;
        }

        if (liveMatch.Bucket is not null)
        {
            multiplier *= liveMatch.Bucket.PromotionStage switch
            {
                "ScaledLive" => 1.08m,
                "LiveProbe" => 1.00m,
                "Paper" => 0.90m,
                _ => 0.85m
            };

            multiplier *= liveMatch.Bucket.Trades switch
            {
                >= 12 => 1.08m,
                >= 8 => 1.04m,
                >= 4 => 1.00m,
                _ => 0.90m
            };

            if (liveMatch.Bucket.HighEdgeTrades >= 3)
            {
                multiplier *= liveMatch.Bucket.HighEdgeProfitFactor >= 1.35 && liveMatch.Bucket.HighEdgeWinRate >= 55
                    ? 1.06m
                    : liveMatch.Bucket.HighEdgeProfitFactor < 1.10 || liveMatch.Bucket.HighEdgeWinRate < 48
                        ? 0.88m
                        : 0.97m;
            }
            else if (liveMatch.Bucket.ProfitFactor < 1.05 || liveMatch.Bucket.WinRate < 45)
            {
                multiplier *= 0.90m;
            }
        }

        return Math.Clamp(multiplier, 0.75m, 1.20m);
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
        var scope = liveMatch.Specificity switch
        {
            4 => "exact exit bucket",
            3 => "symbol exit bucket",
            2 => "session exit bucket",
            1 => "broad exit bucket",
            _ => "unproven exit bucket"
        };

        if (drift is not null && drift.Value.Score >= 0.65)
        {
            return new FinalAllocatorBiasResult(
                0.88m,
                $"{policy} is drifting live vs paper ({drift.Value.Score:F2}) in {scope}.",
                scope);
        }

        if (live.ExpectedValue <= 0m || live.ProfitFactor < 0.95 || live.WinRate < 40)
        {
            return new FinalAllocatorBiasResult(
                0.92m,
                $"{policy} is underperforming live in {scope} (PF {live.ProfitFactor:F2}, WR {live.WinRate:F1}%, EV {live.ExpectedValue:F2}).",
                scope);
        }

        if (drift is not null && drift.Value.Score >= 0.45)
        {
            return new FinalAllocatorBiasResult(
                0.96m,
                $"{policy} has elevated live drift ({drift.Value.Score:F2}) in {scope}.",
                scope);
        }

        if (live.ExpectedValue > 0m && live.ProfitFactor >= 1.40 && live.WinRate >= 58)
        {
            return new FinalAllocatorBiasResult(
                liveMatch.Specificity >= 3 ? 1.08m : 1.05m,
                $"{policy} is winning live in {scope} (PF {live.ProfitFactor:F2}, WR {live.WinRate:F1}%, EV {live.ExpectedValue:F2}).",
                scope);
        }

        if (live.ExpectedValue > 0m && live.ProfitFactor >= 1.20 && live.WinRate >= 50)
        {
            return new FinalAllocatorBiasResult(
                liveMatch.Specificity >= 3 ? 1.04m : 1.02m,
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
        => (trade.ExitPrice - trade.EntryPrice) * trade.Quantity * (trade.Side == TradeSide.Sell ? -1 : 1);

    private static decimal GetPaperPnl(PaperPosition position) => position.RealizedPnL;

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

    private static double ComputeProfitFactor(IEnumerable<decimal> pnlValues)
    {
        var pnlList = pnlValues.ToList();
        if (pnlList.Count == 0)
        {
            return 0;
        }

        var grossProfit = pnlList.Where(pnl => pnl > 0).Sum();
        var grossLoss = Math.Abs(pnlList.Where(pnl => pnl < 0).Sum());
        if (grossLoss <= 0)
        {
            return grossProfit > 0 ? 99 : 0;
        }

        return (double)(grossProfit / grossLoss);
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
