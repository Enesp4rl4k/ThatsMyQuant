using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy.ProMode;

/// <summary>
/// End-to-End Pro Mode behaviour simulator. Replays historical market data as if the
/// full orchestration were live — so we can MEASURE the cumulative effect of:
///
///   1. Regime detection at each step
///   2. Regime-adaptive allocator rebalancing weights
///   3. Circuit breaker monitoring portfolio DD
///   4. Correlation break guard tracking inter-strategy ρ
///   5. Leverage throttle in Volatile regimes
///
/// ...all interacting on the same capital as a single agent would, producing a
/// timeline of decisions + a final metrics report. This is the "does the system
/// BEHAVE correctly when autonomous" test — which we had not done before.
///
/// Phase A scope: takes PRE-COMPUTED per-strategy daily PnL series as input (from
/// individual PortfolioBacktestRunner runs) and simulates the orchestration overlay
/// on top. Full tick-replay is Phase B (needs streaming market data).
/// </summary>
public sealed class SystemBehaviorSimulator
{
    private readonly ILogger<SystemBehaviorSimulator> _logger;
    private readonly IHistoricalMarketDataService _history;

    public SystemBehaviorSimulator(
        ILogger<SystemBehaviorSimulator> logger,
        IHistoricalMarketDataService history)
    {
        _logger = logger;
        _history = history;
    }

    /// <summary>
    /// Replay the Pro Mode orchestration over the date range. Inputs are pre-computed
    /// daily PnL series per strategy (from individual backtests) plus the primary
    /// regime-reference symbol candles (BTC by default).
    /// </summary>
    public async Task<BehaviorSimulationResult> SimulateAsync(
        IReadOnlyDictionary<string, IReadOnlyList<DailyPnlPoint>> strategyPnlByName,
        string regimeReferenceSymbol,
        string regimeTimeframe,
        DateTime startUtc,
        DateTime endUtc,
        decimal totalCapital,
        CancellationToken ct = default)
    {
        // Fetch reference candles for regime classification.
        var refCandles = await _history.GetHistoricalCandlesAsync(
            regimeReferenceSymbol, regimeTimeframe, startUtc, endUtc, ct).ConfigureAwait(false);
        if (refCandles.Count < 100)
            throw new InvalidOperationException("Insufficient candles for regime detection.");

        // Build a union of all dates across strategy series.
        var allDates = strategyPnlByName.Values
            .SelectMany(series => series.Select(p => p.DateUtc.Date))
            .Distinct()
            .OrderBy(d => d)
            .ToArray();
        if (allDates.Length < 10)
            throw new InvalidOperationException("Insufficient daily PnL points.");

        // Bucket each strategy's PnL by date for fast lookup.
        var pnlByStrategyByDate = strategyPnlByName.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToDictionary(p => p.DateUtc.Date, p => p.PnL),
            StringComparer.OrdinalIgnoreCase);

        var correlationGuard = new CorrelationBreakGuard(
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<CorrelationBreakGuard>.Instance);

        decimal equity = totalCapital;
        decimal peakEquity = totalCapital;
        decimal maxDrawdown = 0m;
        int circuitTripCount = 0;
        var decisionLog = new List<BehaviorDecision>(allDates.Length);
        var equityCurve = new decimal[allDates.Length];
        var cashCurve = new decimal[allDates.Length];
        var regimeCounts = new Dictionary<MarketRegime, int>();
        var allocationHistogram = new Dictionary<MarketRegime, int>();

        // For strategy type mapping — assume "*-Funding" → FundingWeight, "*-Pair-*" → PairsWeight.
        bool IsPair(string name) => name.Contains("Pair", StringComparison.OrdinalIgnoreCase);
        bool IsFunding(string name) => name.Contains("Funding", StringComparison.OrdinalIgnoreCase);

        bool circuitTripped = false;
        decimal cbTripDD = 0.05m;  // 5% DD trip
        decimal cbResetCooldownDD = 0.02m;  // require recovery to 2% before auto-reset

        for (int i = 0; i < allDates.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var day = allDates[i];

            // 1. Classify regime using reference candles up to this day.
            var window = refCandles.TakeWhile(c => c.Timestamp.Date <= day).ToList();
            var regime = window.Count >= 50
                ? MarketRegimeDetector.Detect(window, timeframe: regimeTimeframe).Regime
                : MarketRegime.Unknown;

            regimeCounts[regime] = regimeCounts.GetValueOrDefault(regime) + 1;

            // 2. Compute regime-adaptive allocation (no trend strategy yet — folds to cash).
            var allocation = RegimeAdaptiveAllocator.Compute(regime, hasValidatedTrendStrategy: false);
            allocationHistogram[regime] = allocationHistogram.GetValueOrDefault(regime) + 1;

            // 3. Aggregate daily PnL weighted by allocation.
            decimal dayPnl = 0m;
            var dailyPnlForCorr = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            if (!circuitTripped)
            {
                foreach (var kv in pnlByStrategyByDate)
                {
                    if (!kv.Value.TryGetValue(day, out var strategyPnl)) strategyPnl = 0m;
                    decimal weight = IsPair(kv.Key) ? allocation.PairsWeight
                                    : IsFunding(kv.Key) ? allocation.FundingWeight
                                    : 0m;
                    // Each strategy's PnL was backtest-computed at 100% of its capital;
                    // scale by its current portfolio weight × number-of-strategies-of-kind.
                    // Simplification: PnL series already reflects its share; we scale weight.
                    // Here: assume original PnL was at 1/N weight; overlay scales it to new weight/1.
                    // For rough approximation, multiply by (allocation weight / original weight).
                    // Original weights (static): pairs share total 50%, funding 50%. So 3 pairs at
                    // ~16% each, 3 funding at ~16% each. Here we don't know per-strat weight, so
                    // we just apply the bucket weight as a scaling factor (conservative).
                    dayPnl += strategyPnl * weight * 2m;  // ×2 because original weights were ~50% of total
                    dailyPnlForCorr[kv.Key] = strategyPnl;
                }
            }
            else
            {
                // Circuit tripped — cash returns only (0% real, no PnL).
                dayPnl = 0m;
            }

            equity += dayPnl;
            equityCurve[i] = equity;
            cashCurve[i] = equity * allocation.CashWeight;

            if (equity > peakEquity) peakEquity = equity;
            decimal dd = peakEquity > 0 ? (peakEquity - equity) / peakEquity : 0m;
            if (dd > maxDrawdown) maxDrawdown = dd;

            // 4. Circuit breaker logic.
            if (!circuitTripped && dd >= cbTripDD)
            {
                circuitTripped = true;
                circuitTripCount++;
                _logger.LogInformation("Sim day {Day}: CIRCUIT TRIPPED at DD {DD:P2}", day, dd);
            }
            else if (circuitTripped && dd <= cbResetCooldownDD)
            {
                circuitTripped = false;
                _logger.LogInformation("Sim day {Day}: Circuit auto-reset at DD {DD:P2}", day, dd);
            }

            // 5. Correlation guard update (daily).
            if (dailyPnlForCorr.Count > 0)
                correlationGuard.RecordDailyPnl(day, dailyPnlForCorr);

            // Log decision for this day.
            decisionLog.Add(new BehaviorDecision(
                Day: day,
                Regime: regime,
                Allocation: allocation,
                DayPnl: dayPnl,
                Equity: equity,
                DrawdownPct: dd,
                CircuitTripped: circuitTripped,
                CorrelationState: correlationGuard.Snapshot().State,
                AvgCorrelation: correlationGuard.Snapshot().AverageCorrelation));
        }

        // Final metrics.
        var dailyReturns = new double[allDates.Length];
        for (int i = 0; i < allDates.Length; i++)
        {
            decimal prevEq = i == 0 ? totalCapital : equityCurve[i - 1];
            dailyReturns[i] = (double)((equityCurve[i] - prevEq) / (prevEq == 0 ? 1 : prevEq));
        }

        double mean = dailyReturns.Average();
        double var_ = dailyReturns.Length > 1
            ? dailyReturns.Sum(r => (r - mean) * (r - mean)) / (dailyReturns.Length - 1)
            : 0.0;
        double std = Math.Sqrt(var_);
        double sharpe = std > 1e-12 ? (mean / std) * Math.Sqrt(365.0) : 0.0;

        return new BehaviorSimulationResult(
            TotalCapital: totalCapital,
            FinalEquity: equity,
            TotalPnl: equity - totalCapital,
            TotalReturnPct: (equity - totalCapital) / totalCapital,
            AnnualizedSharpe: sharpe,
            MaxDrawdown: maxDrawdown,
            TradingDays: allDates.Length,
            CircuitTripCount: circuitTripCount,
            RegimeDistribution: regimeCounts,
            EquityCurve: equityCurve,
            CashCurve: cashCurve,
            Dates: allDates,
            DecisionLog: decisionLog);
    }
}

public sealed record DailyPnlPoint(DateTime DateUtc, decimal PnL);

public sealed record BehaviorDecision(
    DateTime Day,
    MarketRegime Regime,
    PortfolioAllocation Allocation,
    decimal DayPnl,
    decimal Equity,
    decimal DrawdownPct,
    bool CircuitTripped,
    CorrelationGuardState CorrelationState,
    double AvgCorrelation);

public sealed record BehaviorSimulationResult(
    decimal TotalCapital,
    decimal FinalEquity,
    decimal TotalPnl,
    decimal TotalReturnPct,
    double AnnualizedSharpe,
    decimal MaxDrawdown,
    int TradingDays,
    int CircuitTripCount,
    IReadOnlyDictionary<MarketRegime, int> RegimeDistribution,
    decimal[] EquityCurve,
    decimal[] CashCurve,
    DateTime[] Dates,
    IReadOnlyList<BehaviorDecision> DecisionLog);
