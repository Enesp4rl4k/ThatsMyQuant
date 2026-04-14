using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Walk-Forward Analysis Engine with rolling train/test splits.
/// Validates strategy robustness by training on historical windows and
/// testing on unseen out-of-sample (OOS) data sequentially.
///
/// Flow per window:
///   1. Split candles into train (70%) and test (30%) segments
///   2. Run backtest on train segment to establish baseline metrics
///   3. Run backtest on OOS segment to validate performance
///   4. Compare train vs OOS to detect overfitting
///   5. Roll window forward and repeat
///
/// Final output: Walk-Forward Efficiency (WFE) = OOS_Sharpe / Train_Sharpe
/// WFE > 0.5 indicates robust strategy. WFE < 0.3 = likely overfit.
/// </summary>
public sealed class WalkForwardEngine
{
    private readonly BacktestEngineApp _backtestEngine;
    private readonly ILogger<WalkForwardEngine> _logger;

    public WalkForwardEngine(
        BacktestEngineApp backtestEngine,
        ILogger<WalkForwardEngine> logger)
    {
        _backtestEngine = backtestEngine;
        _logger = logger;
    }

    public async Task<WalkForwardResult> RunAsync(
        List<Candle> candles,
        string symbol,
        int numWindows = 6,
        double trainRatio = 0.7,
        decimal capital = 100_000m,
        CancellationToken ct = default)
    {
        if (candles.Count < 200)
        {
            _logger.LogWarning("Insufficient data for walk-forward: {Count} candles", candles.Count);
            return new WalkForwardResult { Symbol = symbol };
        }

        int windowSize = candles.Count / numWindows;
        int trainSize = (int)(windowSize * trainRatio);
        int testSize = windowSize - trainSize;

        if (trainSize < 80 || testSize < 30)
        {
            _logger.LogWarning("Window sizes too small: train={Train}, test={Test}. Need train>=80, test>=30", trainSize, testSize);
            return new WalkForwardResult { Symbol = symbol };
        }

        _logger.LogInformation(
            "Walk-Forward: {Symbol} | {Windows} windows | {Train}/{Test} split | {Total} candles",
            symbol, numWindows, trainSize, testSize, candles.Count);

        var windowResults = new List<WalkForwardWindowResult>();
        double totalTrainSharpe = 0;
        double totalOosSharpe = 0;

        for (int w = 0; w < numWindows; w++)
        {
            ct.ThrowIfCancellationRequested();

            int startIdx = w * windowSize;
            int trainEnd = startIdx + trainSize;
            int testEnd = Math.Min(trainEnd + testSize, candles.Count);

            if (trainEnd > candles.Count || testEnd - trainEnd < 30)
            {
                _logger.LogDebug("Skipping window {Window}: insufficient data at tail", w);
                break;
            }

            var trainCandles = candles.GetRange(startIdx, trainSize);
            var testCandles = candles.GetRange(trainEnd, testEnd - trainEnd);

            _logger.LogDebug("Window {Window}: train [{TrainStart}-{TrainEnd}], test [{TestStart}-{TestEnd}]",
                w, startIdx, trainEnd, trainEnd, testEnd);

            var trainResult = await _backtestEngine.RunAsync(trainCandles, symbol, capital);
            var oosResult = await _backtestEngine.RunAsync(testCandles, symbol, capital);

            double trainSharpe = ComputeSharpe(trainResult);
            double oosSharpe = ComputeSharpe(oosResult);
            totalTrainSharpe += trainSharpe;
            totalOosSharpe += oosSharpe;

            var trainRegime = trainCandles.Count >= 34
                ? MarketRegimeDetector.Detect(trainCandles).Regime.ToString()
                : "Unknown";
            var oosRegime = testCandles.Count >= 34
                ? MarketRegimeDetector.Detect(testCandles).Regime.ToString()
                : "Unknown";

            windowResults.Add(new WalkForwardWindowResult
            {
                WindowIndex = w,
                TrainStartDate = trainCandles[0].Timestamp,
                TrainEndDate = trainCandles[^1].Timestamp,
                OosStartDate = testCandles[0].Timestamp,
                OosEndDate = testCandles[^1].Timestamp,
                TrainSharpe = trainSharpe,
                OosSharpe = oosSharpe,
                TrainPnL = trainResult.TotalPnL,
                OosPnL = oosResult.TotalPnL,
                TrainTrades = trainResult.TotalTrades,
                OosTrades = oosResult.TotalTrades,
                TrainWinRate = trainResult.WinRate,
                OosWinRate = oosResult.WinRate,
                TrainProfitFactor = trainResult.ProfitFactor,
                OosProfitFactor = oosResult.ProfitFactor,
                TrainMaxDD = (double)trainResult.MaxDrawdownPercent,
                OosMaxDD = (double)oosResult.MaxDrawdownPercent,
                TrainRegime = trainRegime,
                OosRegime = oosRegime
            });

            _logger.LogInformation(
                "Window {Window}: Train Sharpe={TrainSharpe:F2} OOS Sharpe={OosSharpe:F2} | Train PnL={TrainPnL:N2} OOS PnL={OosPnL:N2}",
                w, trainSharpe, oosSharpe, trainResult.TotalPnL, oosResult.TotalPnL);
        }

        return BuildAggregateResult(symbol, windowResults, numWindows, totalTrainSharpe, totalOosSharpe);
    }

    private static WalkForwardResult BuildAggregateResult(
        string symbol,
        List<WalkForwardWindowResult> windows,
        int expectedWindows,
        double totalTrainSharpe,
        double totalOosSharpe)
    {
        if (windows.Count == 0)
            return new WalkForwardResult { Symbol = symbol };

        double avgTrainSharpe = totalTrainSharpe / windows.Count;
        double avgOosSharpe = totalOosSharpe / windows.Count;

        // Walk-Forward Efficiency: ratio of OOS to train performance
        double wfe = Math.Abs(avgTrainSharpe) > 0.001
            ? avgOosSharpe / avgTrainSharpe
            : 0;

        // Regime stability: how often train and OOS regimes match
        int regimeMatches = windows.Count(w => w.TrainRegime == w.OosRegime);
        double regimeStability = (double)regimeMatches / windows.Count;

        // OOS consistency: percentage of windows with positive OOS sharpe
        int positiveOosWindows = windows.Count(w => w.OosSharpe > 0);
        double oosConsistency = (double)positiveOosWindows / windows.Count;

        // Degradation detection: OOS PF should not collapse vs train PF
        double avgTrainPF = windows.Average(w => w.TrainProfitFactor);
        double avgOosPF = windows.Average(w => w.OosProfitFactor);
        double pfDegradation = avgTrainPF > 0 ? (avgTrainPF - avgOosPF) / avgTrainPF : 0;

        bool isRobust = wfe > 0.3
            && avgOosSharpe > 0
            && oosConsistency >= 0.5
            && pfDegradation < 0.6
            && windows.Average(w => w.OosMaxDD) < 30;

        return new WalkForwardResult
        {
            Symbol = symbol,
            WindowResults = windows,
            WalkForwardEfficiency = wfe,
            AggregateTrainSharpe = avgTrainSharpe,
            AggregateOosSharpe = avgOosSharpe,
            AggregateOosPnL = windows.Sum(w => w.OosPnL),
            AggregateOosWinRate = windows.Average(w => w.OosWinRate),
            OosConsistency = oosConsistency,
            RegimeStabilityScore = regimeStability,
            ProfitFactorDegradation = pfDegradation,
            TotalWindows = expectedWindows,
            CompletedWindows = windows.Count,
            IsRobust = isRobust
        };
    }

    private static double ComputeSharpe(BacktestResult result)
    {
        if (result.Trades == null || result.Trades.Count < 2) return 0;
        var returns = result.Trades.Select(t => (double)t.PnL).ToList();
        double mean = returns.Average();
        double variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        double stdDev = Math.Sqrt(Math.Max(variance, 0));
        return stdDev > 0 ? mean / stdDev * Math.Sqrt(252) : 0;
    }
}

public record WalkForwardResult
{
    public string Symbol { get; init; } = "";
    public List<WalkForwardWindowResult> WindowResults { get; init; } = new();
    public double WalkForwardEfficiency { get; init; }
    public double AggregateTrainSharpe { get; init; }
    public double AggregateOosSharpe { get; init; }
    public decimal AggregateOosPnL { get; init; }
    public double AggregateOosWinRate { get; init; }
    public double OosConsistency { get; init; }
    public double RegimeStabilityScore { get; init; }
    public double ProfitFactorDegradation { get; init; }
    public int TotalWindows { get; init; }
    public int CompletedWindows { get; init; }
    public bool IsRobust { get; init; }
}

public record WalkForwardWindowResult
{
    public int WindowIndex { get; init; }
    public DateTime TrainStartDate { get; init; }
    public DateTime TrainEndDate { get; init; }
    public DateTime OosStartDate { get; init; }
    public DateTime OosEndDate { get; init; }
    public double TrainSharpe { get; init; }
    public double OosSharpe { get; init; }
    public decimal TrainPnL { get; init; }
    public decimal OosPnL { get; init; }
    public int TrainTrades { get; init; }
    public int OosTrades { get; init; }
    public double TrainWinRate { get; init; }
    public double OosWinRate { get; init; }
    public double TrainProfitFactor { get; init; }
    public double OosProfitFactor { get; init; }
    public double TrainMaxDD { get; init; }
    public double OosMaxDD { get; init; }
    public string TrainRegime { get; init; } = "";
    public string OosRegime { get; init; } = "";
}
