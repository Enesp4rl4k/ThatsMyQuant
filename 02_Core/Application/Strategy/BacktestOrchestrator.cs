using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Orchestrates the automated backtesting workflow:
/// 1. Download historical data via API.
/// 2. Execute backtest using BacktestEngineApp.
/// 3. Run PBO overfitting validation.
/// 4. Save versioned performance results to the store.
/// 5. Return structured result for regression detection.
/// </summary>
public sealed class BacktestOrchestrator
{
    private readonly IHistoricalMarketDataService _historicalDataService;
    private readonly BacktestEngineApp _backtestEngine;
    private readonly IStrategyPerformanceStore _performanceStore;
    private readonly ILogger<BacktestOrchestrator> _logger;

    /// <summary>
    /// Monotonically increasing version counter for backtest runs.
    /// Resets per application lifetime; persisted snapshots carry the version tag.
    /// </summary>
    private static int _versionCounter;

    public BacktestOrchestrator(
        IHistoricalMarketDataService historicalDataService,
        BacktestEngineApp backtestEngine,
        IStrategyPerformanceStore performanceStore,
        ILogger<BacktestOrchestrator> logger)
    {
        _historicalDataService = historicalDataService;
        _backtestEngine = backtestEngine;
        _performanceStore = performanceStore;
        _logger = logger;
    }

    /// <summary>
    /// Runs an automated backtest session. Returns a structured result for regression tracking.
    /// Throws on infrastructure failures (data fetch, persistence) — callers decide how to handle.
    /// </summary>
    public async Task<BacktestSessionResult> RunAutomatedSessionAsync(
        string symbol,
        string timeframe,
        DateTime start,
        DateTime end,
        decimal capital = 100_000m,
        CancellationToken ct = default)
    {
        var version = $"v{Interlocked.Increment(ref _versionCounter)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        _logger.LogInformation("Starting backtest session {Version}: {Symbol} [{Timeframe}]", version, symbol, timeframe);

        // STEP 1: Download data
        var candles = await _historicalDataService.GetHistoricalCandlesAsync(symbol, timeframe, start, end, ct);
        if (candles.Count < 100)
        {
            _logger.LogWarning("Skipping session {Version}: Insufficient data ({Count} candles)", version, candles.Count);
            return BacktestSessionResult.Skipped(symbol, version, $"Insufficient data: {candles.Count} candles");
        }

        // STEP 2: Run Backtest
        _logger.LogInformation("Data ready. Replaying {Count} candles...", candles.Count);
        var result = await _backtestEngine.RunAsync(candles, symbol, capital);

        // STEP 3: PBO Overfitting Validation (requires sufficient data)
        PboResult? pboResult = null;
        if (candles.Count >= 16 * 60)
        {
            _logger.LogInformation("Running PBO overfitting analysis for {Symbol}...", symbol);
            pboResult = await _backtestEngine.ComputePboAsync(candles, symbol, 16, capital);
            _logger.LogInformation("PBO: {PBO:F3} | Stochastic Dominance: {SD:F3} | Decay: {PD:F3}",
                pboResult.PBO, pboResult.StochasticDominance, pboResult.PerformanceDecay);
        }

        // STEP 4: Save versioned results
        bool pass = EvaluateBacktestPass(result, pboResult);
        var hash = ComputeResultHash(result);
        await StoreResultsAsync(result, timeframe, pboResult, version, hash);

        _logger.LogInformation(
            "Session {Version} completed. PnL: {PnL:F2} | Sharpe: {Sharpe:F2} | MaxDD: {MaxDD:F2}% | Pass: {Pass}",
            version, result.TotalPnL, result.SharpeRatio, result.MaxDrawdownPercent, pass);

        // STEP 5: Cleanup
        candles.Clear();

        return new BacktestSessionResult
        {
            Symbol = symbol,
            Version = version,
            Hash = hash,
            Status = BacktestSessionStatus.Completed,
            BacktestPass = pass,
            TotalTrades = result.TotalTrades,
            TotalPnL = result.TotalPnL,
            SharpeRatio = result.SharpeRatio,
            SortinoRatio = result.SortinoRatio,
            MaxDrawdownPercent = (double)result.MaxDrawdownPercent,
            ProfitFactor = result.ProfitFactor,
            PboScore = pboResult?.PBO ?? -1,
            WinRate = result.WinRate,
            CandleCount = result.Trades.Count
        };
    }

    public static bool EvaluateBacktestPass(BacktestResult result, PboResult? pbo)
    {
        if (result.TotalTrades < 30) return false;
        if (result.TotalPnL <= 0) return false;
        if (result.ProfitFactor < 1.2) return false;
        if (result.SharpeRatio < 0.5) return false;
        if (result.MaxDrawdownPercent > 25) return false;
        if (pbo != null && pbo.PBO > 0.5) return false;
        return true;
    }

    private async Task StoreResultsAsync(BacktestResult result, string timeframe, PboResult? pboResult, string version, string hash)
    {
        var store = await _performanceStore.LoadAsync();
        bool pass = EvaluateBacktestPass(result, pboResult);

        var snapshot = new StrategyBucketPerformanceSnapshot
        {
            Symbol = result.Symbol,
            SessionTag = $"Backtest_{DateTime.UtcNow:yyyyMMdd_HHmm}",
            StrategyTag = "ConsensusSwarm",
            RegimeTag = "Mixed",
            Trades = result.TotalTrades,
            WinRate = result.TotalTrades > 0 ? (double)result.WinningTrades / result.TotalTrades : 0,
            ProfitFactor = result.ProfitFactor,
            TotalPnL = (decimal)result.TotalPnL,
            SortinoRatio = result.SortinoRatio,
            SharpeRatio = result.SharpeRatio,
            MaxDrawdownPercent = (double)result.MaxDrawdownPercent,
            AverageRR = result.AverageRR,
            MaxConsecutiveLosses = result.MaxConsecutiveLosses,
            PboScore = pboResult?.PBO ?? -1,
            StochasticDominance = pboResult?.StochasticDominance ?? 0,
            StartingCapital = result.StartingCapital,
            FinalEquity = result.FinalEquity,
            BacktestVersion = version,
            BacktestHash = hash,
            RecordedAtUtc = DateTime.UtcNow,
            PromotionStage = "Backtest",
            BacktestPass = pass
        };

        store.BacktestBuckets.Add(snapshot);
        await _performanceStore.SaveAsync(store);

        _logger.LogInformation("Results persisted. Version: {Version} | Hash: {Hash} | Pass: {Pass}", version, hash, pass);
    }

    /// <summary>
    /// Compares current backtest result against the most recent persisted result for the same symbol.
    /// Returns a regression report identifying degradation in key metrics.
    /// </summary>
    public async Task<BacktestRegressionReport> DetectRegressionAsync(BacktestSessionResult current)
    {
        var state = await _performanceStore.LoadAsync();
        var previousRuns = state.BacktestBuckets
            .Where(b => b.Symbol == current.Symbol && b.BacktestVersion != current.Version)
            .OrderByDescending(b => b.RecordedAtUtc)
            .ToList();

        if (previousRuns.Count == 0)
        {
            return new BacktestRegressionReport
            {
                Symbol = current.Symbol,
                CurrentVersion = current.Version,
                HasBaseline = false,
                Summary = "No previous backtest to compare against."
            };
        }

        var baseline = previousRuns[0];
        var deltas = new List<string>();
        bool hasRegression = false;

        double sharpeDelta = current.SharpeRatio - baseline.SharpeRatio;
        if (sharpeDelta < -0.3) { deltas.Add($"Sharpe degraded: {baseline.SharpeRatio:F2} -> {current.SharpeRatio:F2}"); hasRegression = true; }

        double pfDelta = current.ProfitFactor - baseline.ProfitFactor;
        if (pfDelta < -0.3) { deltas.Add($"PF degraded: {baseline.ProfitFactor:F2} -> {current.ProfitFactor:F2}"); hasRegression = true; }

        double ddDelta = current.MaxDrawdownPercent - baseline.MaxDrawdownPercent;
        if (ddDelta > 5) { deltas.Add($"MaxDD increased: {baseline.MaxDrawdownPercent:F1}% -> {current.MaxDrawdownPercent:F1}%"); hasRegression = true; }

        double wrDelta = current.WinRate - baseline.WinRate;
        if (wrDelta < -0.1) { deltas.Add($"WinRate dropped: {baseline.WinRate:P1} -> {current.WinRate:P1}"); hasRegression = true; }

        if (baseline.BacktestPass && !current.BacktestPass)
        {
            deltas.Add("Strategy was passing but now FAILS the backtest gate.");
            hasRegression = true;
        }

        var report = new BacktestRegressionReport
        {
            Symbol = current.Symbol,
            CurrentVersion = current.Version,
            CurrentHash = current.Hash,
            BaselineVersion = baseline.BacktestVersion,
            BaselineHash = baseline.BacktestHash,
            HasBaseline = true,
            HasRegression = hasRegression,
            Deltas = deltas,
            Summary = hasRegression
                ? $"REGRESSION DETECTED: {deltas.Count} metric(s) degraded vs {baseline.BacktestVersion}"
                : $"No regression. Performance stable vs {baseline.BacktestVersion}"
        };

        if (hasRegression)
            _logger.LogWarning("Backtest regression for {Symbol}: {Summary}", current.Symbol, report.Summary);
        else
            _logger.LogInformation("No regression for {Symbol}. Hash match: {Match}", current.Symbol, current.Hash == baseline.BacktestHash);

        return report;
    }

    private static string ComputeResultHash(BacktestResult result)
    {
        var raw = $"{result.Symbol}|{result.TotalTrades}|{result.TotalPnL:F4}|{result.SharpeRatio:F6}|{result.MaxDrawdownPercent:F4}|{result.ProfitFactor:F4}";
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..12];
    }
}

public enum BacktestSessionStatus
{
    Completed,
    Skipped,
    Failed
}

public record BacktestSessionResult
{
    public string Symbol { get; init; } = "";
    public string Version { get; init; } = "";
    public string Hash { get; init; } = "";
    public BacktestSessionStatus Status { get; init; }
    public string SkipReason { get; init; } = "";
    public bool BacktestPass { get; init; }
    public int TotalTrades { get; init; }
    public decimal TotalPnL { get; init; }
    public double SharpeRatio { get; init; }
    public double SortinoRatio { get; init; }
    public double MaxDrawdownPercent { get; init; }
    public double ProfitFactor { get; init; }
    public double PboScore { get; init; }
    public double WinRate { get; init; }
    public int CandleCount { get; init; }

    public static BacktestSessionResult Skipped(string symbol, string version, string reason) => new()
    {
        Symbol = symbol,
        Version = version,
        Status = BacktestSessionStatus.Skipped,
        SkipReason = reason
    };
}

public record BacktestRegressionReport
{
    public string Symbol { get; init; } = "";
    public string CurrentVersion { get; init; } = "";
    public string CurrentHash { get; init; } = "";
    public string BaselineVersion { get; init; } = "";
    public string BaselineHash { get; init; } = "";
    public bool HasBaseline { get; init; }
    public bool HasRegression { get; init; }
    public List<string> Deltas { get; init; } = new();
    public string Summary { get; init; } = "";
}
