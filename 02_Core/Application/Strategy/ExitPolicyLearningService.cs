using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Application.Strategy;

public sealed class ExitPolicyLearningService
{
    private readonly IStrategyPerformanceStore _store;
    private readonly object _sync = new();
    private StrategyPerformanceState _state;

    public ExitPolicyLearningService(IStrategyPerformanceStore store)
    {
        _store = store;
        _state = _store.Load();
    }

    public void RecordBacktestState(BacktestResult result)
    {
        lock (_sync)
        {
            UpsertSnapshots(
                result.Trades.GroupBy(trade => new
                {
                    result.Symbol,
                    StrategyTag = string.IsNullOrWhiteSpace(trade.StrategyTag) ? "TrendConfluence" : trade.StrategyTag,
                    RegimeTag = string.IsNullOrWhiteSpace(trade.RegimeTag) ? "Unknown" : trade.RegimeTag,
                    SessionTag = ResolveSessionBucket(trade.ExitTime),
                    ExitPolicyTag = ResolveExitPolicyTag(trade.ExitPolicyTag, trade.StrategyTag),
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
                }));
        }
    }

    public void RecordPaperState(IEnumerable<PaperPosition> closedPositions)
    {
        lock (_sync)
        {
            UpsertSnapshots(
                closedPositions
                    .Where(position => position.ExitTime.HasValue)
                    .GroupBy(position => new
                    {
                        position.Symbol,
                        position.StrategyTag,
                        position.RegimeTag,
                        SessionTag = ResolveSessionBucket(position.ExitTime!.Value),
                        ExitPolicyTag = ResolveExitPolicyTag(position.ExitPolicyTag, position.StrategyTag),
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
                    }));
        }
    }

    public void RecordLiveState(IEnumerable<Trade> closedTrades)
    {
        lock (_sync)
        {
            UpsertSnapshots(
                closedTrades
                    .Where(trade => trade.IsClosed)
                    .GroupBy(trade => new
                    {
                        trade.Symbol,
                        trade.StrategyTag,
                        trade.RegimeTag,
                        SessionTag = ResolveSessionBucket(trade.ExitTime!.Value),
                        ExitPolicyTag = ResolveExitPolicyTag(trade.ExitPolicyTag, trade.StrategyTag),
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
    }

    public TradeDecision ApplyLearnedPolicy(Signal signal, TradeDecision decision, decimal currentPrice, decimal atr)
    {
        lock (_sync)
        {
            _state = _store.Load();
            var recommendation = RecommendPolicy(signal, decision);
            if (recommendation is not { } learnedPolicy ||
                string.Equals(learnedPolicy.ExitPolicyTag, decision.ExitPolicyTag, StringComparison.OrdinalIgnoreCase))
            {
                return decision;
            }

            var profiledDecision = ExitPolicyProfiles.Apply(decision, learnedPolicy.ExitPolicyTag, currentPrice, atr);
            return profiledDecision with
            {
                Reasoning = $"{profiledDecision.Reasoning} | Exit learning promoted {learnedPolicy.ExitPolicyTag} ({learnedPolicy.MatchScope}, score {learnedPolicy.Score:F1}, sample {learnedPolicy.WeightedTrades:F1})."
            };
        }
    }

    public TradeDecision ApplyLiveSizingConfidence(Signal signal, TradeDecision decision)
    {
        lock (_sync)
        {
            _state = _store.Load();
            var effectiveTimestamp = signal.Timestamp == default ? DateTime.UtcNow : signal.Timestamp;
            var sessionTag = ResolveSessionBucket(effectiveTimestamp);
            var candidates = FindMatchingSnapshots(signal.Symbol, decision.StrategyTag, decision.RegimeTag, sessionTag)
                .Where(item => string.Equals(item.Snapshot.ExecutionMode, "REAL", StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(item.Snapshot.ExitPolicyTag, decision.ExitPolicyTag, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (!candidates.Any())
            {
                return decision;
            }

            var weightedTrades = candidates.Sum(item => item.Snapshot.Trades);
            if (weightedTrades < 3)
            {
                return decision;
            }

            var weightedWinRate = SafeWeightedAverage(candidates, item => item.Snapshot.WinRate, item => item.Snapshot.Trades);
            var weightedProfitFactor = SafeWeightedAverage(candidates, item => item.Snapshot.ProfitFactor, item => item.Snapshot.Trades);
            var weightedExpectedValue = candidates.Sum(item => item.Snapshot.ExpectedValue * item.Snapshot.Trades) / weightedTrades;
            var driftScore = ComputePolicyDriftScore(signal, decision, decision.ExitPolicyTag);

            var multiplier = 1.00m;
            var reason = string.Empty;

            if (driftScore >= 0.65)
            {
                multiplier = 0.78m;
                reason = $"Live vs paper drift for {decision.ExitPolicyTag} is severe ({driftScore:F2}).";
            }
            else if (driftScore >= 0.45)
            {
                multiplier = 0.88m;
                reason = $"Live vs paper drift for {decision.ExitPolicyTag} is elevated ({driftScore:F2}).";
            }
            else if (weightedExpectedValue > 0 && weightedProfitFactor >= 1.40 && weightedWinRate >= 58)
            {
                multiplier = 1.15m;
                reason = $"Live exit winner {decision.ExitPolicyTag} is exceptionally healthy (PF {weightedProfitFactor:F2}, WR {weightedWinRate:F1}%, EV {weightedExpectedValue:F2}).";
            }
            else if (weightedExpectedValue > 0 && weightedProfitFactor >= 1.20 && weightedWinRate >= 50)
            {
                multiplier = 1.08m;
                reason = $"Live exit winner {decision.ExitPolicyTag} is healthy (PF {weightedProfitFactor:F2}, WR {weightedWinRate:F1}%, EV {weightedExpectedValue:F2}).";
            }
            else if (weightedExpectedValue <= 0 || weightedProfitFactor < 0.95 || weightedWinRate < 40)
            {
                multiplier = 0.80m;
                reason = $"Live exit winner {decision.ExitPolicyTag} is underperforming (PF {weightedProfitFactor:F2}, WR {weightedWinRate:F1}%, EV {weightedExpectedValue:F2}).";
            }
            else if (weightedProfitFactor < 1.05 || weightedWinRate < 45)
            {
                multiplier = 0.90m;
                reason = $"Live exit winner {decision.ExitPolicyTag} is soft (PF {weightedProfitFactor:F2}, WR {weightedWinRate:F1}%, EV {weightedExpectedValue:F2}).";
            }

            if (multiplier == 1.00m)
            {
                return decision;
            }

            return decision with
            {
                PositionSizePercent = Math.Max(0.001m, decision.PositionSizePercent * multiplier),
                Reasoning = $"{decision.Reasoning} | Exit sizing confidence {multiplier:F2}x: {reason}"
            };
        }
    }

    private ExitPolicyRecommendation? RecommendPolicy(Signal signal, TradeDecision decision)
    {
        var sessionTag = ResolveSessionBucket(signal.Timestamp == default ? DateTime.UtcNow : signal.Timestamp);
        var candidates = FindMatchingSnapshots(signal.Symbol, decision.StrategyTag, decision.RegimeTag, sessionTag);
        if (!candidates.Any())
        {
            return null;
        }

        var policyScores = candidates
            .GroupBy(snapshot => snapshot.Snapshot.ExitPolicyTag)
            .Select(group =>
            {
                var weightedTrades = group.Sum(snapshot => snapshot.Snapshot.Trades * GetModeWeight(snapshot.Snapshot.ExecutionMode));
                var weightedWinRate = SafeWeightedAverage(group, snapshot => snapshot.Snapshot.WinRate, snapshot => snapshot.Snapshot.Trades * GetModeWeight(snapshot.Snapshot.ExecutionMode));
                var weightedProfitFactor = SafeWeightedAverage(group, snapshot => snapshot.Snapshot.ProfitFactor, snapshot => snapshot.Snapshot.Trades * GetModeWeight(snapshot.Snapshot.ExecutionMode));
                var weightedExpectedValue = group.Sum(snapshot => snapshot.Snapshot.ExpectedValue * GetModeWeight(snapshot.Snapshot.ExecutionMode));
                var weightedTotalPnl = group.Sum(snapshot => snapshot.Snapshot.TotalPnL * GetModeWeight(snapshot.Snapshot.ExecutionMode));
                var driftScore = ComputePolicyDriftScore(signal, decision, group.Key);
                var score = (weightedExpectedValue * 0.55m) +
                            ((decimal)weightedProfitFactor * 12m) +
                            ((decimal)weightedWinRate * 0.20m) +
                            (weightedTotalPnl * 0.05m) +
                            ((decimal)weightedTrades * 0.75m) -
                            ((decimal)driftScore * 25m);

                return new ExitPolicyRecommendation(
                    group.Key,
                    ResolveMatchScope(group),
                    score,
                    weightedTrades,
                    weightedProfitFactor,
                    weightedWinRate,
                    weightedExpectedValue);
            })
            .Where(item => item.WeightedTrades >= 2.5m &&
                           item.WeightedExpectedValue > 0 &&
                           item.WeightedProfitFactor >= 1.05 &&
                           item.WeightedWinRate >= 42)
            .OrderByDescending(item => item.Score)
            .ToList();

        if (!policyScores.Any())
        {
            return null;
        }

        var best = policyScores[0];
        var current = policyScores.FirstOrDefault(item => string.Equals(item.ExitPolicyTag, decision.ExitPolicyTag, StringComparison.OrdinalIgnoreCase));
        var hasCurrent = policyScores.Any(item => string.Equals(item.ExitPolicyTag, decision.ExitPolicyTag, StringComparison.OrdinalIgnoreCase));
        if (!hasCurrent)
        {
            return best;
        }

        return best.Score > current.Score * 1.05m ? best : null;
    }

    private IEnumerable<ExitPolicyMatchSnapshot> FindMatchingSnapshots(
        string symbol,
        string strategyTag,
        string regimeTag,
        string sessionTag)
    {
        var exact = _state.ExitPolicyBuckets
            .Where(snapshot =>
                string.Equals(snapshot.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(snapshot.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(snapshot.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(snapshot.SessionTag, sessionTag, StringComparison.OrdinalIgnoreCase))
            .Select(snapshot => new ExitPolicyMatchSnapshot(snapshot, "ExactBucket"))
            .ToList();
        if (exact.Any())
        {
            return exact;
        }

        var symbolSession = _state.ExitPolicyBuckets
            .Where(snapshot =>
                string.Equals(snapshot.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(snapshot.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(snapshot.SessionTag, sessionTag, StringComparison.OrdinalIgnoreCase))
            .Select(snapshot => new ExitPolicyMatchSnapshot(snapshot, "SymbolSession"))
            .ToList();
        if (symbolSession.Any())
        {
            return symbolSession;
        }

        var strategySession = _state.ExitPolicyBuckets
            .Where(snapshot =>
                string.Equals(snapshot.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(snapshot.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(snapshot.SessionTag, sessionTag, StringComparison.OrdinalIgnoreCase))
            .Select(snapshot => new ExitPolicyMatchSnapshot(snapshot, "StrategySession"))
            .ToList();
        if (strategySession.Any())
        {
            return strategySession;
        }

            return _state.ExitPolicyBuckets
                .Where(snapshot =>
                    string.Equals(snapshot.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(snapshot.SessionTag, sessionTag, StringComparison.OrdinalIgnoreCase))
                .Select(snapshot => new ExitPolicyMatchSnapshot(snapshot, "RegimeSession"))
                .ToList();
    }

    private double ComputePolicyDriftScore(Signal signal, TradeDecision decision, string exitPolicyTag)
    {
        var effectiveTimestamp = signal.Timestamp == default ? DateTime.UtcNow : signal.Timestamp;
        var sessionTag = ResolveSessionBucket(effectiveTimestamp);

        var liveCandidates = FindMatchingSnapshots(signal.Symbol, decision.StrategyTag, decision.RegimeTag, sessionTag)
            .Where(item =>
                string.Equals(item.Snapshot.ExecutionMode, "REAL", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Snapshot.ExitPolicyTag, exitPolicyTag, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var paperCandidates = FindMatchingSnapshots(signal.Symbol, decision.StrategyTag, decision.RegimeTag, sessionTag)
            .Where(item =>
                string.Equals(item.Snapshot.ExecutionMode, "PAPER", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Snapshot.ExitPolicyTag, exitPolicyTag, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!liveCandidates.Any() || !paperCandidates.Any())
        {
            return 0;
        }

        var liveTrades = liveCandidates.Sum(item => item.Snapshot.Trades);
        var paperTrades = paperCandidates.Sum(item => item.Snapshot.Trades);
        if (liveTrades < 2 || paperTrades < 2)
        {
            return 0;
        }

        var liveWinRate = SafeWeightedAverage(liveCandidates, item => item.Snapshot.WinRate, item => item.Snapshot.Trades);
        var paperWinRate = SafeWeightedAverage(paperCandidates, item => item.Snapshot.WinRate, item => item.Snapshot.Trades);
        var liveProfitFactor = SafeWeightedAverage(liveCandidates, item => item.Snapshot.ProfitFactor, item => item.Snapshot.Trades);
        var paperProfitFactor = SafeWeightedAverage(paperCandidates, item => item.Snapshot.ProfitFactor, item => item.Snapshot.Trades);
        var liveExpectedValue = liveCandidates.Sum(item => item.Snapshot.ExpectedValue * item.Snapshot.Trades) / liveTrades;
        var paperExpectedValue = paperCandidates.Sum(item => item.Snapshot.ExpectedValue * item.Snapshot.Trades) / paperTrades;

        var winRateDrift = Math.Abs(liveWinRate - paperWinRate) / 100.0;
        var profitFactorBase = Math.Max(0.5, paperProfitFactor);
        var profitFactorDrift = Math.Abs(liveProfitFactor - paperProfitFactor) / profitFactorBase;
        var expectedValueBase = Math.Max(1m, Math.Abs(paperExpectedValue));
        var expectedValueDrift = (double)(Math.Abs(liveExpectedValue - paperExpectedValue) / expectedValueBase);

        return Math.Min(1.50, (winRateDrift * 0.35) + (profitFactorDrift * 0.35) + (expectedValueDrift * 0.30));
    }

    private void UpsertSnapshots(IEnumerable<ExitPolicyPerformanceSnapshot> snapshots)
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
        Persist();
    }

    private void Persist()
    {
        _state.RecordedAtUtc = DateTime.UtcNow;
        _store.Save(_state);
    }

    private static decimal GetModeWeight(string executionMode) => executionMode.ToUpperInvariant() switch
    {
        "REAL" => 1.35m,
        "PAPER" => 1.00m,
        _ => 0.65m
    };

    private static double SafeWeightedAverage<T>(
        IEnumerable<T> items,
        Func<T, double> selector,
        Func<T, decimal> weightSelector)
    {
        var weighted = items
            .Select(item => new { Value = selector(item), Weight = weightSelector(item) })
            .Where(item => item.Weight > 0m)
            .ToList();
        if (!weighted.Any())
        {
            return 0;
        }

        var totalWeight = weighted.Sum(item => item.Weight);
        return totalWeight <= 0m
            ? 0
            : (double)(weighted.Sum(item => (decimal)item.Value * item.Weight) / totalWeight);
    }

    private static string ResolveMatchScope(IGrouping<string, ExitPolicyMatchSnapshot> group)
        => group
            .GroupBy(item => item.MatchScope)
            .OrderByDescending(item => item.Sum(snapshot => snapshot.Snapshot.Trades))
            .Select(item => item.Key)
            .FirstOrDefault() ?? "Fallback";

    private static string ResolveExitPolicyTag(string? exitPolicyTag, string? strategyTag)
    {
        if (!string.IsNullOrWhiteSpace(exitPolicyTag))
        {
            return exitPolicyTag;
        }

        return strategyTag switch
        {
            "RangeReversion" => "MeanReversion",
            "BreakoutHunter" => "BreakoutExpansion",
            "HftMicroEdge" => "HftScalp",
            _ => "TrendRunner"
        };
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

    private static decimal GetLivePnl(Trade trade)
        => (trade.ExitPrice - trade.EntryPrice) * trade.Quantity * (trade.Side == Domain.Enums.TradeSide.Sell ? -1 : 1);

    private readonly record struct ExitPolicyMatchSnapshot(ExitPolicyPerformanceSnapshot Snapshot, string MatchScope);
    private readonly record struct ExitPolicyRecommendation(
        string ExitPolicyTag,
        string MatchScope,
        decimal Score,
        decimal WeightedTrades,
        double WeightedProfitFactor,
        double WeightedWinRate,
        decimal WeightedExpectedValue);
}

internal static class ExitPolicyProfiles
{
    public static TradeDecision Apply(TradeDecision decision, string exitPolicyTag, decimal currentPrice, decimal atr)
    {
        var (stopAtr, takeAtr, partialProgress, partialExitFraction, breakevenFraction, maxHoldingBars) = exitPolicyTag switch
        {
            "MeanReversion" => (0.95m, 1.60m, 0.60m, 0.55m, 0.35m, 8),
            "BreakoutExpansion" => (1.45m, 3.40m, 0.35m, 0.33m, 0.25m, 12),
            "HftScalp" => (0.45m, 0.75m, 0.30m, 0.25m, 0.20m, 4),
            _ => (1.25m, 3.20m, 0.45m, 0.40m, 0.55m, 18)
        };

        var stopLoss = decision.Side == Domain.Enums.TradeSide.Buy
            ? currentPrice - (atr * stopAtr)
            : currentPrice + (atr * stopAtr);
        var takeProfit = decision.Side == Domain.Enums.TradeSide.Buy
            ? currentPrice + (atr * takeAtr)
            : currentPrice - (atr * takeAtr);
        var partialTakeProfit = decision.Side == Domain.Enums.TradeSide.Buy
            ? currentPrice + ((takeProfit - currentPrice) * partialProgress)
            : currentPrice - ((currentPrice - takeProfit) * partialProgress);

        return decision with
        {
            ExitPolicyTag = exitPolicyTag,
            StopLoss = stopLoss,
            TakeProfit = takeProfit,
            PartialTakeProfit = partialTakeProfit,
            PartialExitFraction = partialExitFraction,
            BreakevenTriggerFraction = breakevenFraction,
            MaxHoldingBars = maxHoldingBars
        };
    }
}
