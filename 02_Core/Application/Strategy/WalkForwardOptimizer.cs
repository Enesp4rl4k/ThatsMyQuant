using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Phase B — Walk-Forward Optimizer.
///
/// Unlike <see cref="WalkForwardEngine"/> (which only *measures* OOS degradation
/// with hardcoded params), this optimizer:
///   1. On each train window, grid-searches a small parameter space.
///   2. Selects the parameter set that maximizes *deflated* Sharpe on train.
///   3. Applies only that best-on-train set to the forward-in-time test window.
///   4. Aggregates out-of-sample results across all rolling windows.
///
/// This is the structural lever that breaks PBO: params are chosen
/// from history alone, so OOS performance is a true estimate of live performance.
///
/// Train grid search uses <see cref="BacktestEngineApp.RunAsyncWithProfileOverlay"/> plus optional
/// parallel evaluation (<c>Backtest:Performance:EnableParallelWfoGridSearch</c>) so workers never
/// mutate shared <c>IOptionsMonitor</c> profile state.
/// </summary>
public sealed class WalkForwardOptimizer
{
    private readonly BacktestEngineApp _backtestEngine;
    private readonly IOptionsMonitor<BacktestTimeframeProfileOptions> _profileOptions;
    private readonly ILogger<WalkForwardOptimizer> _logger;
    private readonly BacktestPerformanceOptions _perfOptions;
    private readonly RegimeExpectancyService? _regimeExpectancy;

    public WalkForwardOptimizer(
        BacktestEngineApp backtestEngine,
        IOptionsMonitor<BacktestTimeframeProfileOptions> profileOptions,
        ILogger<WalkForwardOptimizer> logger,
        IOptions<BacktestPerformanceOptions>? performanceOptions = null,
        RegimeExpectancyService? regimeExpectancy = null)
    {
        _backtestEngine = backtestEngine;
        _profileOptions = profileOptions;
        _logger = logger;
        _perfOptions = performanceOptions?.Value ?? new BacktestPerformanceOptions();
        _regimeExpectancy = regimeExpectancy;
    }

    // Deliberately small grid — large spaces re-introduce overfitting they aim to prevent.
    // Each axis has 3 points; full cross = 27 combos per train window.
    public static readonly int[] ConfidenceGrid = new[] { 58, 62, 66 };
    public static readonly decimal[] RiskRewardGrid = new[] { 1.3m, 1.5m, 1.8m };
    public static readonly decimal[] TrendCapGrid = new[] { 6.0m, 8.0m, 10.0m };

    public async Task<WfoResult> RunAsync(
        List<Candle> candles,
        string symbol,
        string timeframe = "1h",
        int numWindows = 5,
        double trainRatio = 0.7,
        decimal capital = 100_000m,
        CancellationToken ct = default)
    {
        // Always restore injected options profile so callers (orchestrator, CLI) never inherit
        // a half-finished WFO mutation after this method returns.
        var sessionBaseline = SnapshotProfile(timeframe);
        try
        {
            if (candles.Count < 200)
            {
                _logger.LogWarning("WFO: Insufficient data ({Count} candles) for {Symbol}", candles.Count, symbol);
                return new WfoResult { Symbol = symbol };
            }

            int windowSize = candles.Count / numWindows;
            int trainSize = (int)(windowSize * trainRatio);
            int testSize = windowSize - trainSize;
            if (trainSize < WalkForwardConstants.MinTrainBars || testSize < WalkForwardConstants.MinTestBars)
            {
                _logger.LogWarning("WFO: window too small (train={T}, test={S})", trainSize, testSize);
                return new WfoResult { Symbol = symbol };
            }

            var windows = new List<WfoWindow>();
            decimal totalOosPnL = 0m;
            int totalOosTrades = 0;
            int totalOosWins = 0;

            for (int w = 0; w < numWindows; w++)
            {
                ct.ThrowIfCancellationRequested();
                int s = w * windowSize;
                int te = s + trainSize;
                int oe = Math.Min(te + testSize, candles.Count);
                if (te > candles.Count || oe - te < WalkForwardConstants.MinTestBars) break;

                var train = candles.GetRange(s, trainSize);
                var test = candles.GetRange(te, oe - te);

                // Grid search on train (parallel when enabled — uses profile overlay, no options mutation)
                var best = await GridSearchOnTrainAsync(train, symbol, timeframe, capital, ct).ConfigureAwait(false);

                // OOS test with winning params (overlay only)
                var oosResult = await _backtestEngine.RunAsyncWithProfileOverlay(test, symbol, timeframe, capital, best.Params)
                    .ConfigureAwait(false);

                // Feed OOS trades into the per-regime expectancy store. These are out-of-sample
                // by construction (params fit on train, applied to disjoint test window), so
                // aggregating them cannot introduce in-sample bias to live signal gating.
                if (_regimeExpectancy is not null && oosResult.Trades?.Count > 0)
                {
                    _regimeExpectancy.RecordOos(oosResult.Trades);
                }

                var oosSharpe = oosResult.SharpeRatio;
                var oosPnL = oosResult.TotalPnL;
                var oosTrades = oosResult.TotalTrades;
                var oosWr = oosResult.WinRate;

                totalOosPnL += oosPnL;
                totalOosTrades += oosTrades;
                totalOosWins += (int)Math.Round(oosTrades * oosWr);

                windows.Add(new WfoWindow
                {
                    Index = w,
                    BestParams = best.Params,
                    TrainSharpe = best.Sharpe,
                    OosSharpe = (double)oosSharpe,
                    OosPnL = oosPnL,
                    OosTrades = oosTrades,
                    OosWinRate = oosWr
                });

                _logger.LogInformation(
                    "WFO window {W}: best={Best} | train SR={TrainSR:F2} → OOS SR={OosSR:F2} PnL={PnL:F0} Trades={Tr}",
                    w, best.Params, best.Sharpe, oosSharpe, oosPnL, oosTrades);
            }

            double avgOosSharpe = windows.Count > 0 ? windows.Average(w => w.OosSharpe) : 0;
            double oosConsistency = windows.Count > 0 ? windows.Count(w => w.OosPnL > 0) / (double)windows.Count : 0;
            double aggregateWinRate = totalOosTrades > 0 ? (double)totalOosWins / totalOosTrades : 0;

            return new WfoResult
            {
                Symbol = symbol,
                Windows = windows,
                AggregateOosPnL = totalOosPnL,
                AggregateOosTrades = totalOosTrades,
                AggregateOosWinRate = aggregateWinRate,
                AverageOosSharpe = avgOosSharpe,
                OosConsistency = oosConsistency,
                // WFO is robust when >= 60% of windows are profitable OOS AND average OOS Sharpe > 0.
                IsRobust = oosConsistency >= 0.6 && avgOosSharpe > 0
            };
        }
        finally
        {
            ApplyParams(timeframe, sessionBaseline);
        }
    }

    /// <summary>Reads MinConfidence / MinRiskReward / TrendPositionCap from the live options profile.</summary>
    public ParamSet SnapshotParamSet(string timeframe) => SnapshotProfile(timeframe);

    /// <summary>Writes the three tunables into the live options profile for the given timeframe.</summary>
    public void ApplyParamSet(string timeframe, ParamSet ps) => ApplyParams(timeframe, ps);

    /// <summary>
    /// Params from the most recent WFO window's train winner — the usual freeze for the next live segment.
    /// </summary>
    public static ParamSet? SelectPrimaryReplayParams(WfoResult wfo)
        => wfo.Windows.Count == 0 ? null : wfo.Windows[^1].BestParams;

    private async Task<(ParamSet Params, double Sharpe)> GridSearchOnTrainAsync(
        List<Candle> train,
        string symbol,
        string timeframe,
        decimal capital,
        CancellationToken ct)
    {
        var candidates = new List<ParamSet>(ConfidenceGrid.Length * RiskRewardGrid.Length * TrendCapGrid.Length);
        foreach (var conf in ConfidenceGrid)
        {
            foreach (var rr in RiskRewardGrid)
            {
                foreach (var cap in TrendCapGrid)
                {
                    candidates.Add(new ParamSet(conf, rr, cap));
                }
            }
        }

        var results = new ConcurrentBag<(ParamSet p, double sr)>();

        async Task EvaluateAsync(ParamSet p)
        {
            var r = await _backtestEngine.RunAsyncWithProfileOverlay(train, symbol, timeframe, capital, p).ConfigureAwait(false);
            results.Add((p, DeflatedSharpe(r)));
        }

        if (_perfOptions.EnableParallelWfoGridSearch && candidates.Count > 1)
        {
            int dop = _perfOptions.WfoGridMaxDegreeOfParallelism > 0
                ? Math.Max(1, _perfOptions.WfoGridMaxDegreeOfParallelism)
                : Math.Max(2, Environment.ProcessorCount - 1);

            await Parallel.ForEachAsync(
                candidates,
                new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct },
                async (p, token) =>
                {
                    _ = token;
                    await EvaluateAsync(p).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        else
        {
            foreach (var p in candidates)
            {
                await EvaluateAsync(p).ConfigureAwait(false);
            }
        }

        var winner = results.OrderByDescending(c => c.sr).ThenBy(c => c.p.MinConfidence).ThenBy(c => c.p.MinRiskReward).First();
        return (winner.p, winner.sr);
    }

    private static double DeflatedSharpe(BacktestResult r)
    {
        if (r.Trades == null || r.Trades.Count < 2) return 0.0;
        var returns = r.Trades.Select(t => (double)t.PnL).ToList();
        double mean = returns.Average();
        double variance = returns.Sum(x => (x - mean) * (x - mean)) / (returns.Count - 1);
        double sd = Math.Sqrt(Math.Max(variance, 0));
        if (sd <= 0) return 0.0;
        double raw = mean / sd * Math.Sqrt(252);
        double n = returns.Count;
        return raw * (n / (n + 20.0));
    }

    private ParamSet SnapshotProfile(string timeframe)
    {
        var p = ResolveProfile(timeframe);
        return new ParamSet(p.MinConfidence, p.MinRiskReward, p.TrendPositionCapPercent);
    }

    private void ApplyParams(string timeframe, ParamSet ps)
    {
        var p = ResolveProfile(timeframe);
        p.MinConfidence = ps.MinConfidence;
        p.MinRiskReward = ps.MinRiskReward;
        p.TrendPositionCapPercent = ps.TrendCapPercent;
    }

    private BacktestProfile ResolveProfile(string timeframe) => timeframe switch
    {
        "1m" => _profileOptions.CurrentValue.OneMinute,
        "5m" => _profileOptions.CurrentValue.FiveMinute,
        "15m" => _profileOptions.CurrentValue.FifteenMinute,
        "4h" => _profileOptions.CurrentValue.FourHour,
        "1d" => _profileOptions.CurrentValue.OneDay,
        _ => _profileOptions.CurrentValue.OneHour
    };
}

public readonly record struct ParamSet(int MinConfidence, decimal MinRiskReward, decimal TrendCapPercent)
{
    public override string ToString() => $"conf={MinConfidence} rr={MinRiskReward:F2} cap={TrendCapPercent:F1}%";
}

public record WfoWindow
{
    public int Index { get; init; }
    public ParamSet BestParams { get; init; }
    public double TrainSharpe { get; init; }
    public double OosSharpe { get; init; }
    public decimal OosPnL { get; init; }
    public int OosTrades { get; init; }
    public double OosWinRate { get; init; }
}

public record WfoResult
{
    public string Symbol { get; init; } = "";
    public List<WfoWindow> Windows { get; init; } = new();
    public decimal AggregateOosPnL { get; init; }
    public int AggregateOosTrades { get; init; }
    public double AggregateOosWinRate { get; init; }
    public double AverageOosSharpe { get; init; }
    public double OosConsistency { get; init; }
    public bool IsRobust { get; init; }
}
