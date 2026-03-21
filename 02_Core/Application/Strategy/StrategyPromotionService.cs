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
        _state = _store.Load();
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
                    liveBucket.ScaledLiveEligible && liveBucket.AverageEdgeScore >= 85m && liveBucket.HighEdgeProfitFactor >= 1.35 ? 1.10m : 1.00m,
                    "ScaledLive",
                    capitalBiasMultiplier),
                $"Promotion guard confirmed. Drift={driftScore:F2}, live EV={liveBucket.ExpectedValue:F2}, PF={liveBucket.ProfitFactor:F2}, slip={liveBucket.AverageEntrySlippagePercent:F3}%, latency={liveBucket.AverageExecutionLatencyMs:F0}ms. {capitalBiasLabel}",
                true,
                driftScore,
                "ScaledLive");
        }
    }

    public void RecordBacktestState(BacktestResult result)
    {
        lock (_sync)
        {
            var snapshots = result.Trades
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
                        AverageEdgeScore = 0m,
                        HighEdgeTrades = 0,
                        HighEdgeWinRate = 0,
                        HighEdgeProfitFactor = 0,
                        HighEdgeExpectedValue = 0m,
                        HighEdgeTotalPnL = 0m,
                        AverageEntrySlippagePercent = 0m,
                        AverageExecutionLatencyMs = 0,
                        BacktestPass = backtestPass,
                        PaperPass = false,
                        LiveProbePass = false,
                        ScaledLiveEligible = false,
                        PromotionReady = backtestPass,
                        PromotionStage = "Backtest",
                        RecordedAtUtc = DateTime.UtcNow
                    };
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

            _state.BacktestBuckets = _state.BacktestBuckets
                .OrderByDescending(snapshot => snapshot.RecordedAtUtc)
                .ToList();

            Persist();
        }
    }

    public void RecordPaperState(IEnumerable<PaperPosition> closedPositions)
    {
        lock (_sync)
        {
            _state.PaperBuckets = closedPositions
                .Where(position => position.ExitTime.HasValue)
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
                        AverageEdgeScore = 0m,
                        HighEdgeTrades = 0,
                        HighEdgeWinRate = 0,
                        HighEdgeProfitFactor = 0,
                        HighEdgeExpectedValue = 0m,
                        HighEdgeTotalPnL = 0m,
                        AverageEntrySlippagePercent = 0m,
                        AverageExecutionLatencyMs = 0,
                        BacktestPass = backtestPass,
                        PaperPass = paperPass,
                        LiveProbePass = false,
                        ScaledLiveEligible = false,
                        PromotionReady = paperPass,
                        PromotionStage = paperPass ? "Paper" : "Backtest",
                        RecordedAtUtc = DateTime.UtcNow
                    };
                })
                .OrderByDescending(snapshot => snapshot.RecordedAtUtc)
                .ToList();

            Persist();
        }
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

        Persist();
    }

    private void Persist()
    {
        _state.RecordedAtUtc = DateTime.UtcNow;
        _store.Save(_state);
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
        var strongest = Math.Max(liveMatch.Specificity, Math.Max(paperMatch.Specificity, backtestMatch.Specificity));
        var scope = strongest switch
        {
            4 => "exact symbol-session bucket",
            3 => "symbol bucket",
            2 => "session bucket",
            1 => "broad strategy bucket",
            _ => "unproven bucket"
        };

        return $"Capital bias {capitalBiasMultiplier:F2}x from {scope}.";
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
}
