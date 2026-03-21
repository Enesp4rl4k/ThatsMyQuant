using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Application.Strategy;

public sealed class StrategyPromotionService
{
    private readonly IStrategyPerformanceStore _store;
    private StrategyPerformanceState _state;

    public StrategyPromotionService(IStrategyPerformanceStore store)
    {
        _store = store;
        _state = _store.Load();
    }

    public PromotionGuardResult Evaluate(Signal signal, TradeDecision decision, IReadOnlyList<Trade> liveTrades)
    {
        RefreshLiveState(liveTrades);

        var sessionTag = ResolveSessionBucket(signal.Timestamp);
        var liveBucket = FindBestBucket(_state.LiveBuckets, signal.Symbol, decision.StrategyTag, decision.RegimeTag, sessionTag);
        var paperBucket = FindBestBucket(_state.PaperBuckets, signal.Symbol, decision.StrategyTag, decision.RegimeTag, sessionTag);

        if (paperBucket is null || paperBucket.Trades < 5)
        {
            return new PromotionGuardResult(
                true,
                1.00m,
                "Promotion guard: no strong paper baseline yet, proceed with normal engine controls.",
                false,
                0);
        }

        if (!paperBucket.PromotionReady)
        {
            return new PromotionGuardResult(
                true,
                0.70m,
                $"Paper bucket not yet promotion-ready for {decision.StrategyTag}/{decision.RegimeTag}.",
                false,
                0);
        }

        if (liveBucket is null || liveBucket.Trades < 4)
        {
            return new PromotionGuardResult(
                true,
                0.60m,
                $"Promotion guard: paper-ready bucket, but live sample is thin ({liveBucket?.Trades ?? 0} trades). Start smaller.",
                true,
                0);
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
                driftScore);
        }

        if (driftScore >= 0.40 || liveBucket.ExpectedValue <= 0 || liveBucket.ProfitFactor < 1.0)
        {
            return new PromotionGuardResult(
                true,
                0.55m,
                $"Paper/live drift caution: drift={driftScore:F2}, live EV={liveBucket.ExpectedValue:F2}, PF={liveBucket.ProfitFactor:F2}, slip={liveBucket.AverageEntrySlippagePercent:F3}%, latency={liveBucket.AverageExecutionLatencyMs:F0}ms.",
                false,
                driftScore);
        }

        return new PromotionGuardResult(
            true,
            1.00m,
            $"Promotion guard confirmed. Drift={driftScore:F2}, live EV={liveBucket.ExpectedValue:F2}, PF={liveBucket.ProfitFactor:F2}, slip={liveBucket.AverageEntrySlippagePercent:F3}%, latency={liveBucket.AverageExecutionLatencyMs:F0}ms.",
            true,
            driftScore);
    }

    public void RecordPaperState(IEnumerable<PaperPosition> closedPositions)
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
                    AverageEntrySlippagePercent = 0m,
                    AverageExecutionLatencyMs = 0,
                    PromotionReady = pnlValues.Count >= 8 &&
                                     ComputeProfitFactor(pnlValues) >= 1.20 &&
                                     pnlValues.Average() > 0 &&
                                     pnlValues.Count(value => value > 0) * 1.0 / pnlValues.Count >= 0.45,
                    RecordedAtUtc = DateTime.UtcNow
                };
            })
            .OrderByDescending(snapshot => snapshot.RecordedAtUtc)
            .ToList();

        Persist();
    }

    public StrategyPerformanceState GetState() => _state;

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
                    AverageEntrySlippagePercent = group.Any() ? group.Average(trade => trade.EntrySlippagePercent) : 0m,
                    AverageExecutionLatencyMs = group.Any() ? group.Average(trade => (double)trade.ExecutionLatencyMs) : 0,
                    PromotionReady = pnlValues.Count >= 8 &&
                                     ComputeProfitFactor(pnlValues) >= 1.20 &&
                                     pnlValues.Average() > 0 &&
                                     pnlValues.Count(value => value > 0) * 1.0 / pnlValues.Count >= 0.45,
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

    private static StrategyBucketPerformanceSnapshot? FindBestBucket(
        IEnumerable<StrategyBucketPerformanceSnapshot> buckets,
        string symbol,
        string strategyTag,
        string regimeTag,
        string sessionTag)
    {
        return buckets.FirstOrDefault(bucket =>
                   string.Equals(bucket.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(bucket.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(bucket.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(bucket.SessionTag, sessionTag, StringComparison.OrdinalIgnoreCase))
               ?? buckets.FirstOrDefault(bucket =>
                   string.Equals(bucket.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(bucket.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(bucket.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase))
               ?? buckets.FirstOrDefault(bucket =>
                   string.Equals(bucket.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(bucket.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(bucket.SessionTag, sessionTag, StringComparison.OrdinalIgnoreCase))
               ?? buckets.FirstOrDefault(bucket =>
                   string.Equals(bucket.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(bucket.RegimeTag, regimeTag, StringComparison.OrdinalIgnoreCase));
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

    private static double ComputeProfitFactor(IEnumerable<decimal> pnlValues)
    {
        var grossProfit = pnlValues.Where(pnl => pnl > 0).Sum();
        var grossLoss = Math.Abs(pnlValues.Where(pnl => pnl < 0).Sum());
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
}
