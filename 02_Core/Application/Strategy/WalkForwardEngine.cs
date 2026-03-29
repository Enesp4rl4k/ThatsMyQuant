using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Application.MachineLearning;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Walk-forward analysis engine for out-of-sample validation.
///
/// Instead of a single backtest (which overfits to the entire dataset), walk-forward:
///   1. Splits data into sequential windows: [train₁|test₁], [train₂|test₂], ...
///   2. Trains the ML model on each training window
///   3. Tests on the immediately following out-of-sample window
///   4. Aggregates out-of-sample results for true performance estimation
///
/// This is the gold standard for strategy validation because:
///   - No future data leakage (each test window only uses past training data)
///   - Captures strategy decay (if edge erodes over time, walk-forward shows it)
///   - Multiple independent test periods reduce luck variance
/// </summary>
public sealed class WalkForwardEngine
{
    private readonly ITechnicalAnalysisService _technicalAnalysis;
    private readonly IAgentCoordinator _agentCoordinator;
    private readonly ILogger<WalkForwardEngine> _logger;

    public WalkForwardEngine(
        ITechnicalAnalysisService technicalAnalysis,
        IAgentCoordinator agentCoordinator,
        ILogger<WalkForwardEngine> logger)
    {
        _technicalAnalysis = technicalAnalysis;
        _agentCoordinator = agentCoordinator;
        _logger = logger;
    }

    /// <summary>
    /// Runs walk-forward analysis on historical candle data.
    /// </summary>
    /// <param name="allCandles">Full historical dataset, sorted oldest-first</param>
    /// <param name="symbol">Trading symbol</param>
    /// <param name="trainBars">Number of bars in each training window</param>
    /// <param name="testBars">Number of bars in each test window</param>
    /// <param name="stepBars">How many bars to slide forward between windows</param>
    /// <param name="startingCapital">Starting capital per window</param>
    public async Task<WalkForwardResult> RunAsync(
        List<Candle> allCandles,
        string symbol,
        int trainBars = 500,
        int testBars = 100,
        int stepBars = 100,
        decimal startingCapital = 100_000m,
        CancellationToken ct = default)
    {
        if (allCandles.Count < trainBars + testBars)
        {
            _logger.LogWarning("[WalkForward] Insufficient data: {Count} candles, need {Min}",
                allCandles.Count, trainBars + testBars);
            return new WalkForwardResult { Symbol = symbol };
        }

        var windows = new List<WindowResult>();
        var allOosTrades = new List<BacktestTradeResult>();
        int windowIndex = 0;

        for (int start = 0; start + trainBars + testBars <= allCandles.Count; start += stepBars)
        {
            ct.ThrowIfCancellationRequested();

            var trainData = allCandles.GetRange(start, trainBars);
            var testData = allCandles.GetRange(start + trainBars, testBars);

            // Include overlap for indicator warmup — backtest needs 60 bars of history
            int warmupBars = Math.Min(60, trainBars);
            var testWithWarmup = allCandles.GetRange(start + trainBars - warmupBars, testBars + warmupBars);

            _logger.LogInformation("[WalkForward] Window {Index}: train {TrainStart:d}→{TrainEnd:d}, test {TestStart:d}→{TestEnd:d}",
                windowIndex, trainData[0].Timestamp, trainData[^1].Timestamp,
                testData[0].Timestamp, testData[^1].Timestamp);

            // 1. Train ML model on this window's training data
            var trainingSet = FeatureSetBuilder.BuildTrainingSet(trainData);
            double rSquared = 0;
            if (trainingSet.Count >= 100)
            {
                var trainer = new AlphaModelTrainer(
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<AlphaModelTrainer>.Instance);
                rSquared = trainer.TrainAndSaveModel(trainingSet, $"wf_model_{windowIndex}.zip");
            }

            // 2. Backtest on out-of-sample data
            var backtester = new BacktestEngineApp(_technicalAnalysis, _agentCoordinator,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<BacktestEngineApp>.Instance);
            var result = await backtester.RunAsync(testWithWarmup, symbol, startingCapital).ConfigureAwait(false);

            var windowResult = new WindowResult
            {
                WindowIndex = windowIndex,
                TrainStart = trainData[0].Timestamp,
                TrainEnd = trainData[^1].Timestamp,
                TestStart = testData[0].Timestamp,
                TestEnd = testData[^1].Timestamp,
                TrainSamples = trainingSet.Count,
                ModelRSquared = rSquared,
                BacktestResult = result
            };

            windows.Add(windowResult);
            allOosTrades.AddRange(result.Trades);
            windowIndex++;

            // Cleanup temp model file
            try { File.Delete($"wf_model_{windowIndex - 1}.zip"); } catch { /* best-effort */ }
        }

        return BuildAggregateResult(symbol, windows, allOosTrades, startingCapital);
    }

    private WalkForwardResult BuildAggregateResult(
        string symbol,
        List<WindowResult> windows,
        List<BacktestTradeResult> allOosTrades,
        decimal startingCapital)
    {
        int totalTrades = allOosTrades.Count;
        int wins = allOosTrades.Count(t => t.PnL > 0);
        decimal totalPnl = allOosTrades.Sum(t => t.PnL);
        decimal grossProfit = allOosTrades.Where(t => t.PnL > 0).Sum(t => t.PnL);
        decimal grossLoss = Math.Abs(allOosTrades.Where(t => t.PnL <= 0).Sum(t => t.PnL));

        // Compute Sharpe from per-trade returns
        double sharpe = 0, sortino = 0;
        if (allOosTrades.Count >= 2)
        {
            var returns = allOosTrades.Select(t => (double)t.PnLPercent).ToArray();
            double mean = returns.Average();
            double stddev = Math.Sqrt(returns.Select(r => (r - mean) * (r - mean)).Average());
            sharpe = stddev > 0 ? mean / stddev * Math.Sqrt(252) : 0;

            var downside = returns.Where(r => r < 0).Select(r => r * r).ToArray();
            double downsideDev = downside.Length > 0 ? Math.Sqrt(downside.Average()) : 0;
            sortino = downsideDev > 0 ? mean / downsideDev * Math.Sqrt(252) : 0;
        }

        // Max drawdown across all windows
        decimal maxDd = windows.Count > 0 ? windows.Max(w => w.BacktestResult.MaxDrawdownPercent) : 0;

        // Consecutive losses
        int maxConsecLosses = 0, currentStreak = 0;
        foreach (var t in allOosTrades.OrderBy(t => t.EntryTime))
        {
            if (t.PnL <= 0) { currentStreak++; maxConsecLosses = Math.Max(maxConsecLosses, currentStreak); }
            else currentStreak = 0;
        }

        // Model quality across windows
        double avgRSquared = windows.Where(w => w.ModelRSquared > 0).Select(w => w.ModelRSquared).DefaultIfEmpty(0).Average();

        var result = new WalkForwardResult
        {
            Symbol = symbol,
            TotalWindows = windows.Count,
            TotalOosTrades = totalTrades,
            WinningTrades = wins,
            LosingTrades = totalTrades - wins,
            WinRate = totalTrades > 0 ? wins * 100.0 / totalTrades : 0,
            TotalPnL = totalPnl,
            ProfitFactor = grossLoss > 0 ? (double)(grossProfit / grossLoss) : totalPnl > 0 ? 9.99 : 0,
            SharpeRatio = sharpe,
            SortinoRatio = sortino,
            MaxDrawdownPercent = maxDd,
            MaxConsecutiveLosses = maxConsecLosses,
            AverageModelRSquared = avgRSquared,
            StartingCapital = startingCapital,
            Windows = windows,
            AllOosTrades = allOosTrades
        };

        _logger.LogInformation(
            "[WalkForward] {Symbol} — {Windows} windows, {Trades} OOS trades | " +
            "PnL: {PnL:C} | WR: {WR:F1}% | PF: {PF:F2} | Sharpe: {Sharpe:F2} | MaxDD: {DD:F2}%",
            symbol, windows.Count, totalTrades, totalPnl,
            result.WinRate, result.ProfitFactor, sharpe, maxDd);

        return result;
    }
}

/// <summary>Aggregated walk-forward analysis result across all windows.</summary>
public record WalkForwardResult
{
    public string Symbol { get; init; } = "";
    public int TotalWindows { get; init; }
    public int TotalOosTrades { get; init; }
    public int WinningTrades { get; init; }
    public int LosingTrades { get; init; }
    public double WinRate { get; init; }
    public decimal TotalPnL { get; init; }
    public double ProfitFactor { get; init; }
    public double SharpeRatio { get; init; }
    public double SortinoRatio { get; init; }
    public decimal MaxDrawdownPercent { get; init; }
    public int MaxConsecutiveLosses { get; init; }
    public double AverageModelRSquared { get; init; }
    public decimal StartingCapital { get; init; }
    public List<WindowResult> Windows { get; init; } = new();
    public List<BacktestTradeResult> AllOosTrades { get; init; } = new();
}

/// <summary>Result from a single walk-forward window.</summary>
public record WindowResult
{
    public int WindowIndex { get; init; }
    public DateTime TrainStart { get; init; }
    public DateTime TrainEnd { get; init; }
    public DateTime TestStart { get; init; }
    public DateTime TestEnd { get; init; }
    public int TrainSamples { get; init; }
    public double ModelRSquared { get; init; }
    public BacktestResult BacktestResult { get; init; } = new();
}
