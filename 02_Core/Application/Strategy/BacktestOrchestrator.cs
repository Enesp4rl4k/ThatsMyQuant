using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Options;

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
    private readonly BacktestWalkForwardGateOptions _walkForwardGateOptions;
    private readonly BacktestQualityGateOptions _qualityGateOptions;
    private readonly ProfitMaximizationOptions _profitOptions;
    private readonly BacktestParityOptions _parityOptions;
    private readonly WalkForwardOptimizer _walkForwardOptimizer;

    /// <summary>
    /// Monotonically increasing version counter for backtest runs.
    /// Resets per application lifetime; persisted snapshots carry the version tag.
    /// </summary>
    private static int _versionCounter;

    public BacktestOrchestrator(
        IHistoricalMarketDataService historicalDataService,
        BacktestEngineApp backtestEngine,
        IStrategyPerformanceStore performanceStore,
        ILogger<BacktestOrchestrator> logger,
        WalkForwardOptimizer walkForwardOptimizer,
        IOptions<BacktestWalkForwardGateOptions>? walkForwardGateOptions = null,
        IOptions<BacktestQualityGateOptions>? qualityGateOptions = null,
        IOptions<ProfitMaximizationOptions>? profitOptions = null,
        IOptions<BacktestParityOptions>? parityOptions = null)
    {
        _historicalDataService = historicalDataService;
        _backtestEngine = backtestEngine;
        _performanceStore = performanceStore;
        _logger = logger;
        _walkForwardOptimizer = walkForwardOptimizer;
        _walkForwardGateOptions = walkForwardGateOptions?.Value ?? new BacktestWalkForwardGateOptions();
        _qualityGateOptions = qualityGateOptions?.Value ?? new BacktestQualityGateOptions();
        _profitOptions = profitOptions?.Value ?? new ProfitMaximizationOptions();
        _parityOptions = parityOptions?.Value ?? new BacktestParityOptions();
    }

    /// <summary>
    /// Runs an automated backtest session. Returns a structured result for regression tracking.
    /// Throws on infrastructure failures (data fetch, persistence) — callers decide how to handle.
    /// </summary>
    public async Task<BacktestSessionResult> RunHftSessionAsync(
        string symbol,
        string timeframe,
        DateTime start,
        DateTime end,
        decimal capital,
        CancellationToken ct = default)
    {
        var version = $"hft_v{Interlocked.Increment(ref _versionCounter)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        _logger.LogInformation("Starting HFT-only session {Version}: {Symbol} [{Timeframe}]", version, symbol, timeframe);

        var candles = await _historicalDataService.GetHistoricalCandlesAsync(symbol, timeframe, start, end, ct);
        if (candles.Count < 100)
            return BacktestSessionResult.Skipped(symbol, version, $"Insufficient data: {candles.Count} candles");

        var result = await _backtestEngine.RunHftAsync(candles, symbol, timeframe, capital);
        var pass = result.TotalTrades >= 30 && result.TotalPnL > 0 && result.SharpeRatio > 0.5 && result.MaxDrawdownPercent < 25;

        _logger.LogInformation(
            "HFT session {Version} done. PnL={PnL:F2} Sharpe={Sharpe:F2} Trades={Trades} DD={DD:F2}%",
            version, result.TotalPnL, result.SharpeRatio, result.TotalTrades, result.MaxDrawdownPercent);

        return new BacktestSessionResult
        {
            Symbol = symbol,
            Version = version,
            Status = BacktestSessionStatus.Completed,
            BacktestPass = pass,
            TotalTrades = result.TotalTrades,
            TotalPnL = result.TotalPnL,
            SharpeRatio = result.SharpeRatio,
            SortinoRatio = result.SortinoRatio,
            MaxDrawdownPercent = (double)result.MaxDrawdownPercent,
            ProfitFactor = result.ProfitFactor,
            WinRate = result.WinRate,
            AverageRR = result.AverageRR,
            WinningTrades = result.WinningTrades,
            LosingTrades = result.LosingTrades,
            AverageWin = result.WinningTrades > 0 ? result.Trades.Where(t => t.PnL > 0).Average(t => t.PnL) : 0m,
            AverageLoss = result.LosingTrades > 0 ? result.Trades.Where(t => t.PnL <= 0).Average(t => t.PnL) : 0m,
            PboScore = -1,
            CandleCount = result.Trades.Count
        };
    }

    public async Task<BacktestSessionResult> RunHftTickReplaySessionAsync(
        string symbol,
        string timeframe,
        DateTime start,
        DateTime end,
        decimal capital,
        string ticksCsvPath,
        string orderBooksCsvPath,
        CancellationToken ct = default)
    {
        var version = $"hft_l2_v{Interlocked.Increment(ref _versionCounter)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        _logger.LogInformation("Starting HFT L2 tick-replay session {Version}: {Symbol} [{Timeframe}]", version, symbol, timeframe);

        var candles = await _historicalDataService.GetHistoricalCandlesAsync(symbol, timeframe, start, end, ct);
        if (candles.Count < 100)
            return BacktestSessionResult.Skipped(symbol, version, $"Insufficient base candles: {candles.Count}");

        var result = await _backtestEngine.RunTickReplayFromCsvAsync(
            ticksCsvPath,
            orderBooksCsvPath,
            candles,
            symbol,
            capital,
            ct);

        var pass = result.TotalTrades >= 30 && result.TotalPnL > 0 && result.SharpeRatio > 0.5 && result.MaxDrawdownPercent < 25;
        _logger.LogInformation(
            "HFT L2 session {Version} done. PnL={PnL:F2} Sharpe={Sharpe:F2} Trades={Trades} DD={DD:F2}%",
            version, result.TotalPnL, result.SharpeRatio, result.TotalTrades, result.MaxDrawdownPercent);

        return new BacktestSessionResult
        {
            Symbol = symbol,
            Version = version,
            Status = BacktestSessionStatus.Completed,
            BacktestPass = pass,
            TotalTrades = result.TotalTrades,
            TotalPnL = result.TotalPnL,
            SharpeRatio = result.SharpeRatio,
            SortinoRatio = result.SortinoRatio,
            MaxDrawdownPercent = (double)result.MaxDrawdownPercent,
            ProfitFactor = result.ProfitFactor,
            WinRate = result.WinRate,
            AverageRR = result.AverageRR,
            WinningTrades = result.WinningTrades,
            LosingTrades = result.LosingTrades,
            AverageWin = result.WinningTrades > 0 ? result.Trades.Where(t => t.PnL > 0).Average(t => t.PnL) : 0m,
            AverageLoss = result.LosingTrades > 0 ? result.Trades.Where(t => t.PnL <= 0).Average(t => t.PnL) : 0m,
            PboScore = -1,
            CandleCount = result.Trades.Count
        };
    }

    public async Task<BacktestSessionResult> RunAutomatedSessionAsync(
        string symbol,
        string timeframe,
        DateTime start,
        DateTime end,
        decimal capital = 100_000m,
        string riskProfile = "Moderate",
        bool fastScreen = false,
        CancellationToken ct = default)
    {
        var version = $"v{Interlocked.Increment(ref _versionCounter)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        _logger.LogInformation("Starting backtest session {Version}: {Symbol} [{Timeframe}]", version, symbol, timeframe);

        // STEP 1: Download data. Fetch base + HTF in parallel when mapping exists.
        var htfTimeframe = ResolveHigherTimeframe(timeframe);
        var baseCandlesTask = _historicalDataService.GetHistoricalCandlesAsync(symbol, timeframe, start, end, ct);
        Task<List<Candle>>? htfCandlesTask = htfTimeframe is null
            ? null
            : _historicalDataService.GetHistoricalCandlesAsync(symbol, htfTimeframe, start, end, ct);

        await baseCandlesTask.ConfigureAwait(false);
        if (htfCandlesTask is not null)
        {
            try
            {
                await htfCandlesTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch HTF {Htf} for {Symbol}; continuing without MTF gate.", htfTimeframe, symbol);
            }
        }

        var rawCandles = baseCandlesTask.Result;
        var (candles, hygieneReport) = CandleHygieneService.Sanitize(rawCandles, logger: _logger);
        if (hygieneReport.DroppedZeroPrice > 0 || hygieneReport.FlaggedGaps > 0)
        {
            _logger.LogWarning(
                "Data hygiene: dropped {Zero} zero-price, flagged {Gaps} gap bars (>5%) for {Symbol}",
                hygieneReport.DroppedZeroPrice, hygieneReport.FlaggedGaps, symbol);
        }
        if (CandleHygieneService.IsPotentiallyDelisted(candles, end, TimeSpan.FromDays(2), out var delistReason))
        {
            _logger.LogWarning("Delisting/data-truncation advisory for {Symbol}: {Reason}", symbol, delistReason);
        }
        var expectedBaseCandles = EstimateExpectedCandleCount(start, end, timeframe);
        var minRequiredBaseCandles = Math.Max(100, (int)Math.Floor(expectedBaseCandles * 0.70));
        if (candles.Count < 100)
        {
            _logger.LogWarning("Skipping session {Version}: Insufficient data ({Count} candles)", version, candles.Count);
            return BacktestSessionResult.Skipped(symbol, version, $"Insufficient data: {candles.Count} candles");
        }
        if (candles.Count < minRequiredBaseCandles)
        {
            _logger.LogWarning(
                "Skipping session {Version}: Data integrity gate failed for {Symbol}. Got {Actual} candles, expected >= {Required} (est={Expected})",
                version,
                symbol,
                candles.Count,
                minRequiredBaseCandles,
                expectedBaseCandles);
            return BacktestSessionResult.Skipped(
                symbol,
                version,
                $"Data integrity failed: {candles.Count}<{minRequiredBaseCandles} for {timeframe} ({start:yyyy-MM-dd}->{end:yyyy-MM-dd})");
        }

        // STEP 2: Run Backtest (optional WFO profile selection → primary replay → always restore baseline)
        _logger.LogInformation("Data ready. Replaying {Count} candles...", candles.Count);
        var normalizedRiskProfile = string.IsNullOrWhiteSpace(riskProfile) ? "Moderate" : riskProfile.Trim();

        if (htfTimeframe != null)
        {
            var htfCandles = htfCandlesTask?.Status == TaskStatus.RanToCompletion
                ? htfCandlesTask.Result
                : null;
            if (htfCandles is { Count: > 0 })
            {
                _backtestEngine.SetHigherTimeframeCandles(htfCandles);
                _logger.LogInformation("HTF ({Htf}) candles for MTF gate: {Count}", htfTimeframe, htfCandles.Count);
            }
            else
            {
                _backtestEngine.SetHigherTimeframeCandles(null);
            }
        }
        else
        {
            _backtestEngine.SetHigherTimeframeCandles(null);
        }

        var profileBaseline = _walkForwardOptimizer.SnapshotParamSet(timeframe);
        var wfoProfileApplied = false;
        var wfoParamsSummary = "";
        var wfoParamSelectionRobust = false;
        BacktestResult result;
        PboResult? pboResult = null;
        RollingWalkForwardGateResult walkForward = RollingWalkForwardGateResult.Empty(symbol);
        try
        {
            // Reserve a tail holdout that WFO never sees. PBO runs exclusively on this holdout so
            // grid-searched params cannot overfit to the PBO evaluation set. When holdout is
            // disabled or history is too short, fall back to full-series PBO (legacy behavior).
            List<Candle> wfoCandles = candles;
            List<Candle>? pboHoldoutCandles = null;
            if (!fastScreen
                && _walkForwardGateOptions.UsePboHoldout
                && _walkForwardGateOptions.PboHoldoutRatio > 0
                && candles.Count >= _walkForwardGateOptions.MinCandlesForWfoProfileSelection)
            {
                int holdoutSize = (int)(candles.Count * _walkForwardGateOptions.PboHoldoutRatio);
                int optSize = candles.Count - holdoutSize;
                if (optSize >= 200 && holdoutSize >= 8 * 60)
                {
                    wfoCandles = candles.GetRange(0, optSize);
                    pboHoldoutCandles = candles.GetRange(optSize, holdoutSize);
                    _logger.LogInformation(
                        "PBO holdout split: WFO/opt candles={Opt}, PBO holdout candles={Hold} ({Ratio:P0})",
                        optSize, holdoutSize, _walkForwardGateOptions.PboHoldoutRatio);
                }
            }

            if (!fastScreen
                && _walkForwardGateOptions.UseWfoSelectedParamsForPrimaryBacktest
                && wfoCandles.Count >= _walkForwardGateOptions.MinCandlesForWfoProfileSelection)
            {
                _logger.LogInformation(
                    "WFO profile selection enabled: grid-search on {Windows} windows (min candles={Min})",
                    _walkForwardGateOptions.WfoProfileNumWindows,
                    _walkForwardGateOptions.MinCandlesForWfoProfileSelection);

                var wfo = await _walkForwardOptimizer.RunAsync(
                    wfoCandles,
                    symbol,
                    timeframe,
                    _walkForwardGateOptions.WfoProfileNumWindows,
                    _walkForwardGateOptions.WfoProfileTrainRatio,
                    capital,
                    ct).ConfigureAwait(false);

                var chosen = WalkForwardOptimizer.SelectPrimaryReplayParams(wfo);
                if (chosen is { } p)
                {
                    _walkForwardOptimizer.ApplyParamSet(timeframe, p);
                    wfoProfileApplied = true;
                    wfoParamsSummary = p.ToString();
                    wfoParamSelectionRobust = wfo.IsRobust;
                    _logger.LogInformation(
                        "Primary replay using WFO params: {Params} | WFO aggregate OOS PnL={PnL:F2} robust={Robust}",
                        p,
                        wfo.AggregateOosPnL,
                        wfo.IsRobust);
                }
                else
                {
                    _logger.LogWarning("WFO profile selection returned no windows; primary replay uses configured profile.");
                }
            }

            result = await _backtestEngine.RunAsync(candles, symbol, timeframe, normalizedRiskProfile, capital)
                .ConfigureAwait(false);

            // STEP 3: PBO — run on holdout-only when reserved (primary fix for WFO→PBO leakage);
            // otherwise fall back to full-series (legacy). Partition mode defaults to Contiguous so
            // regime clusters stay intact and regime-overfit params are exposed honestly.
            var pboCandles = pboHoldoutCandles ?? candles;
            if (!fastScreen && pboCandles.Count >= 8 * 60)
            {
                var partitionMode = Enum.TryParse<PboPartitionMode>(_walkForwardGateOptions.PboPartitionMode, true, out var pm)
                    ? pm : PboPartitionMode.Contiguous;
                _logger.LogInformation(
                    "Running PBO ({Mode}) for {Symbol} on {Count} candles ({Source})",
                    partitionMode, symbol, pboCandles.Count, pboHoldoutCandles is null ? "full series" : "holdout");
                pboResult = await _backtestEngine.ComputePboAsync(pboCandles, symbol, 8, capital, partitionMode);
                _logger.LogInformation("PBO: {PBO:F3} | Stochastic Dominance: {SD:F3} | Decay: {PD:F3}",
                    pboResult.PBO, pboResult.StochasticDominance, pboResult.PerformanceDecay);
            }

            // STEP 3.5: Rolling Walk-Forward gate (60d train / 20d test / 10d step)
            walkForward = fastScreen
                ? RollingWalkForwardGateResult.Empty(symbol)
                : await RunRollingWalkForwardGateAsync(candles, symbol, timeframe, capital, normalizedRiskProfile, ct);
        }
        finally
        {
            _walkForwardOptimizer.ApplyParamSet(timeframe, profileBaseline);
        }

        // STEP 3.6: Export reports (trade log + equity curve)
        var tradeLogPath = BacktestReportExporter.ExportTradeLog(result, version);
        var equityCurvePath = BacktestReportExporter.ExportEquityCurve(result, version);

        // STEP 4: Save versioned results
        var horizonDays = Math.Max(1, (int)Math.Round((end - start).TotalDays));
        var missingCoverage = ResolveMissingCoverage(result.CoverageFlags, _parityOptions.RequiredCoverageFlags);
        var parityPassed = missingCoverage == DecisionCoverageFlags.None;
        var mlReadyPassed = EvaluateMlReadiness(result, _parityOptions);
        bool pass = EvaluateBacktestPass(
            result,
            pboResult,
            walkForward,
            horizonDays,
            _qualityGateOptions,
            _parityOptions.RequireParityCoverage,
            parityPassed,
            _parityOptions.RequireMlModelAvailability,
            mlReadyPassed);
        var hardGatePass = EvaluateProfitHardGate(result, pboResult, walkForward, normalizedRiskProfile, _profitOptions, _walkForwardGateOptions.MinStochasticDominance);
        var objectiveScore = ComputeObjectiveScore(result, normalizedRiskProfile, _profitOptions);
        var blockers = ComputeReadinessBlockers(result, pboResult, walkForward, horizonDays, _qualityGateOptions, missingCoverage, parityPassed, mlReadyPassed, _parityOptions);
        var expectancyPerTrade = result.TotalTrades > 0 ? result.TotalPnL / result.TotalTrades : 0m;
        var hash = ComputeResultHash(result);
        await StoreResultsAsync(result, timeframe, pboResult, walkForward, version, hash);

        _logger.LogInformation(
            "Session {Version} completed. Mode={Mode} PnL: {PnL:F2} | Sharpe: {Sharpe:F2} | MaxDD: {MaxDD:F2}% | Pass: {Pass}",
            version, fastScreen ? "FAST_SCREEN" : "FULL", result.TotalPnL, result.SharpeRatio, result.MaxDrawdownPercent, pass);

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
            WalkForwardEfficiency = walkForward.WalkForwardEfficiency,
            WalkForwardPassed = walkForward.Passed,
            WalkForwardWindows = walkForward.CompletedWindows,
            WalkForwardGateApplied = walkForward.GateApplied,
            WalkForwardGateReason = walkForward.GateReason,
            RiskProfile = normalizedRiskProfile,
            HardGatePassed = hardGatePass,
            ObjectiveScore = objectiveScore,
            CoverageFlags = result.CoverageFlags,
            CoverageSummary = result.CoverageSummary,
            EntryRejectReasons = result.EntryRejectReasons,
            MissingCoverageFlags = missingCoverage,
            ParityPassed = parityPassed,
            InferenceModelAvailable = result.InferenceModelAvailable,
            InferencePredictionValid = result.InferencePredictionValid,
            MlReadyPassed = mlReadyPassed,
            ReadinessBlockers = blockers,
            WinRate = result.WinRate,
            AverageRR = result.AverageRR,
            WinningTrades = result.WinningTrades,
            LosingTrades = result.LosingTrades,
            AverageWin = result.WinningTrades > 0
                ? result.Trades.Where(t => t.PnL > 0).Average(t => t.PnL)
                : 0m,
            AverageLoss = result.LosingTrades > 0
                ? result.Trades.Where(t => t.PnL <= 0).Average(t => t.PnL)
                : 0m,
            ExpectancyPerTrade = expectancyPerTrade,
            CandleCount = result.Trades.Count,
            TradeLogPath = tradeLogPath,
            EquityCurvePath = equityCurvePath,
            WfoProfileApplied = wfoProfileApplied,
            WfoPrimaryParamsSummary = wfoParamsSummary,
            WfoParamSelectionRobust = wfoParamSelectionRobust
        };
    }

    public static bool EvaluateBacktestPass(
        BacktestResult result,
        PboResult? pbo,
        RollingWalkForwardGateResult? walkForward = null,
        int horizonDays = 365,
        BacktestQualityGateOptions? qualityGate = null,
        bool requireParityCoverage = false,
        bool parityPassed = true,
        bool requireMlModelAvailability = false,
        bool mlReadyPassed = true)
    {
        var gate = qualityGate ?? new BacktestQualityGateOptions();
        var expectancyPerTrade = result.TotalTrades > 0 ? result.TotalPnL / result.TotalTrades : 0m;
        if (result.TotalTrades < gate.MinTrades) return false;
        if (result.MaxDrawdownPercent > gate.MaxDrawdownPercent) return false;
        if (result.ProfitFactor < gate.MinProfitFactor) return false;
        if (result.WinRate < gate.MinWinRatePercent) return false;
        if (expectancyPerTrade < gate.MinExpectancyPerTrade) return false;
        if (pbo != null && pbo.PBO > gate.MaxPbo) return false;
        if (walkForward is not null && walkForward.GateApplied)
        {
            if (!walkForward.Passed) return false;
            if (walkForward.CompletedWindows < gate.MinWalkForwardWindows) return false;
            if (walkForward.OosPositiveRatio < gate.MinOosPositiveRatio) return false;
        }
        if (requireParityCoverage && !parityPassed) return false;
        if (requireMlModelAvailability && !mlReadyPassed) return false;

        var isShortHorizon = horizonDays <= gate.ShortHorizonDays;
        if (isShortHorizon)
        {
            if (result.TotalPnL < gate.ShortMinPnl) return false;
            if (result.SharpeRatio < gate.ShortMinSharpe) return false;
            if (result.AverageRR < gate.ShortMinAverageRr) return false;
            return true;
        }

        if (result.TotalPnL < gate.LongMinPnl) return false;
        if (result.SharpeRatio < gate.LongMinSharpe) return false;
        if (result.AverageRR < gate.LongMinAverageRr) return false;
        return true;
    }

    private async Task StoreResultsAsync(BacktestResult result, string timeframe, PboResult? pboResult, RollingWalkForwardGateResult? walkForward, string version, string hash)
    {
        var store = await _performanceStore.LoadAsync();
        var horizonDays = result.StartDate != default && result.EndDate != default
            ? Math.Max(1, (int)Math.Round((result.EndDate - result.StartDate).TotalDays))
            : _qualityGateOptions.LongHorizonDays;
        var parityPassed = ResolveMissingCoverage(result.CoverageFlags, _parityOptions.RequiredCoverageFlags) == DecisionCoverageFlags.None;
        var mlReadyPassed = EvaluateMlReadiness(result, _parityOptions);
        bool pass = EvaluateBacktestPass(
            result,
            pboResult,
            walkForward,
            horizonDays,
            _qualityGateOptions,
            _parityOptions.RequireParityCoverage,
            parityPassed,
            _parityOptions.RequireMlModelAvailability,
            mlReadyPassed);

        var snapshot = new StrategyBucketPerformanceSnapshot
        {
            Symbol = result.Symbol,
            SessionTag = $"Backtest_{DateTime.UtcNow:yyyyMMdd_HHmm}",
            StrategyTag = DominantTag(result.Trades, t => t.StrategyTag, "ConsensusSwarm"),
            RegimeTag = DominantTag(result.Trades, t => t.RegimeTag, "Mixed"),
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

    private async Task<RollingWalkForwardGateResult> RunRollingWalkForwardGateAsync(
        List<Candle> candles,
        string symbol,
        string timeframe,
        decimal capital,
        string riskProfile,
        CancellationToken ct)
    {
        var trainSpan = TimeSpan.FromDays(Math.Max(1, _walkForwardGateOptions.TrainDays));
        var testSpan = TimeSpan.FromDays(Math.Max(1, _walkForwardGateOptions.TestDays));
        var stepSpan = TimeSpan.FromDays(Math.Max(1, _walkForwardGateOptions.StepDays));
        var minTrainCandles = Math.Max(10, _walkForwardGateOptions.MinTrainCandles);
        var minTestCandles = Math.Max(10, _walkForwardGateOptions.MinTestCandles);
        var minWindows = Math.Max(1, _walkForwardGateOptions.MinWindows);
        var minWfe = _walkForwardGateOptions.MinWalkForwardEfficiency;
        var minOosSharpe = _walkForwardGateOptions.MinOosSharpe;
        var minPositiveOosRatio = _walkForwardGateOptions.MinPositiveOosRatio;
        var minHorizonDays = Math.Max(30, _walkForwardGateOptions.MinHorizonDaysForEnforcement);

        if (candles.Count == 0)
        {
            return RollingWalkForwardGateResult.Empty(symbol);
        }

        var ordered = candles.OrderBy(c => c.Timestamp).ToList();
        var first = ordered[0].Timestamp;
        var last = ordered[^1].Timestamp;
        var horizonDays = Math.Max(0, (last - first).TotalDays);
        var gateApplied = horizonDays >= minHorizonDays;
        var anchor = first;
        // Pool per-trade returns across all windows so Sharpe is computed once on a real sample
        // size (22 windows × ~3 trades = ~66 OOS trades) rather than per-window on n=2..6, where
        // the return-std-dev denominator collapses and produces Sharpes of ±150. See WF-DIAG run
        // on 2026-04-18: per-window avg was −10.87 (nonsense) while pooled OOS was modestly
        // positive. Option #3 of the WF structural fight.
        var pooledTrainReturns = new List<double>();
        var pooledOosReturns = new List<double>();
        var windows = new List<(decimal OosPnL, int OosTradeCount)>();

        while (anchor + trainSpan + testSpan <= last)
        {
            ct.ThrowIfCancellationRequested();

            var trainStart = anchor;
            var trainEnd = anchor + trainSpan;
            var testEnd = trainEnd + testSpan;

            var trainCandles = ordered.Where(c => c.Timestamp >= trainStart && c.Timestamp < trainEnd).ToList();
            var testCandles = ordered.Where(c => c.Timestamp >= trainEnd && c.Timestamp < testEnd).ToList();

            if (trainCandles.Count >= minTrainCandles && testCandles.Count >= minTestCandles)
            {
                var train = await _backtestEngine.RunAsync(trainCandles, symbol, timeframe, riskProfile, capital);
                var test = await _backtestEngine.RunAsync(testCandles, symbol, timeframe, riskProfile, capital);
                if (train.Trades != null)
                    pooledTrainReturns.AddRange(train.Trades.Select(t => (double)t.PnL));
                if (test.Trades != null)
                    pooledOosReturns.AddRange(test.Trades.Select(t => (double)t.PnL));
                windows.Add((test.TotalPnL, test.Trades?.Count ?? 0));
            }

            anchor += stepSpan;
        }

        if (windows.Count == 0)
        {
            return RollingWalkForwardGateResult.Empty(symbol);
        }

        // Pooled Sharpe on all OOS trades — the whole point of WF is to stitch OOS slices into
        // one out-of-sample track record. Sharpe should be computed on that track, not averaged
        // over slice-Sharpes.
        var pooledTrainSharpe = ComputeSharpeFromReturns(pooledTrainReturns);
        var pooledOosSharpe = ComputeSharpeFromReturns(pooledOosReturns);
        // WFE with a sanity denominator: if train Sharpe is within noise of zero, WFE is not a
        // meaningful ratio (the prior implementation's ±32 readings were sign-cancellation
        // artifacts of a tiny denominator). Fall back to pass-through so the other criteria
        // decide the gate.
        // WFE is defined as OOS/Train degradation. That quantity only has meaning when train
        // Sharpe is positive and non-trivial — if train is negative, OOS beating train is a
        // *good* outcome, not something to penalize with a negative ratio. The 2026-04-18
        // bkiu428c0 run exposed this: train=-0.120, OOS=+1.298, WFE=-10.85 blocking a strategy
        // whose OOS track record is strictly better than its train track record. Require
        // pooledTrainSharpe > 0.3 before WFE is admitted into the pass decision.
        var wfeMeaningful = pooledTrainSharpe > 0.3;
        var wfe = wfeMeaningful ? pooledOosSharpe / pooledTrainSharpe : 0d;
        // Per-window positive-PnL ratio (not Sharpe-based — PnL sign is robust to small n).
        var oosPositiveRatio = windows.Count(w => w.OosPnL > 0) / (double)windows.Count;
        var aggregateOosPnl = windows.Sum(w => w.OosPnL);
        var passed =
            windows.Count >= minWindows &&
            pooledOosSharpe >= minOosSharpe &&
            aggregateOosPnl > 0m &&
            (!wfeMeaningful || wfe >= minWfe) &&
            oosPositiveRatio >= minPositiveOosRatio;

        _logger.LogInformation(
            "[WF-DIAG] {Symbol} windows={N} pooledTrades(train={TT}/oos={OT}) pooledTrainSharpe={PT:F3} pooledOosSharpe={PO:F3} WFE={W:F2}{Note} oosPosRatio={R:P0} aggPnL={P:F2} pass(windows>={MW}:{P1} pooledOos>={MO:F2}:{P2} aggPnL>0:{P3} wfe>={MF:F2}:{P4} posRatio>={MP:P0}:{P5})",
            symbol, windows.Count, pooledTrainReturns.Count, pooledOosReturns.Count,
            pooledTrainSharpe, pooledOosSharpe, wfe,
            wfeMeaningful ? "" : " (skipped: train Sharpe ~= 0)",
            oosPositiveRatio, aggregateOosPnl,
            minWindows, windows.Count >= minWindows,
            minOosSharpe, pooledOosSharpe >= minOosSharpe,
            aggregateOosPnl > 0m,
            minWfe, !wfeMeaningful || wfe >= minWfe,
            minPositiveOosRatio, oosPositiveRatio >= minPositiveOosRatio);

        return new RollingWalkForwardGateResult
        {
            Symbol = symbol,
            CompletedWindows = windows.Count,
            WalkForwardEfficiency = wfe,
            AggregateOosSharpe = pooledOosSharpe,
            AggregateOosPnL = aggregateOosPnl,
            OosPositiveRatio = oosPositiveRatio,
            Passed = passed,
            GateApplied = gateApplied,
            GateReason = gateApplied
                ? $"Applied (horizon {horizonDays:F0}d >= {minHorizonDays}d)"
                : $"Not enforced on short horizon {horizonDays:F0}d (< {minHorizonDays}d)"
        };
    }

    private static double ComputeSharpeFromReturns(List<double> returns)
    {
        if (returns.Count < 2) return 0d;
        var mean = returns.Average();
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        var stdDev = Math.Sqrt(Math.Max(variance, 0d));
        return stdDev > 0d ? mean / stdDev * Math.Sqrt(252) : 0d;
    }

    private static double ComputeSharpe(BacktestResult result)
    {
        if (result.Trades == null || result.Trades.Count < 2)
        {
            return 0d;
        }

        var returns = result.Trades.Select(t => (double)t.PnL).ToList();
        var mean = returns.Average();
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        var stdDev = Math.Sqrt(Math.Max(variance, 0d));
        return stdDev > 0d ? mean / stdDev * Math.Sqrt(252) : 0d;
    }

    public static bool EvaluateProfitHardGate(
        BacktestResult result,
        PboResult? pbo,
        RollingWalkForwardGateResult? walkForward,
        string riskProfile,
        ProfitMaximizationOptions? options = null,
        double minStochasticDominance = 0.70)
    {
        var config = (options ?? new ProfitMaximizationOptions()).Resolve(riskProfile);
        var expectancy = result.TotalTrades > 0 ? result.TotalPnL / result.TotalTrades : 0m;
        var wfOk = walkForward is null || !walkForward.GateApplied || walkForward.Passed;
        // PBO must be <= 0.5 AND stochastic dominance >= threshold. Both gates close the loophole
        // where a few lucky OOS segments (low PBO) mask broad test-side failure.
        var pboOk = pbo is null
                    || (pbo.PBO <= 0.5 && pbo.StochasticDominance >= minStochasticDominance);
        return result.TotalPnL > 0m
               && result.SharpeRatio >= config.MinSharpe
               && result.AverageRR >= config.MinAverageRr
               && result.WinRate >= config.MinWinRatePercent
               && expectancy >= config.MinExpectancy
               && wfOk
               && pboOk;
    }

    public static double ComputeObjectiveScore(
        BacktestResult result,
        string riskProfile,
        ProfitMaximizationOptions? options = null)
    {
        var config = (options ?? new ProfitMaximizationOptions()).Resolve(riskProfile);
        var normPnl = result.StartingCapital > 0m ? (double)(result.TotalPnL / result.StartingCapital) : 0d;
        var normSharpe = Math.Clamp(result.SharpeRatio / 2.0, -1.0, 1.0);
        var normRr = Math.Clamp(result.AverageRR / 2.0, 0.0, 1.0);
        var ddPenalty = Math.Clamp((double)result.MaxDrawdownPercent / 25.0, 0.0, 1.0);
        return config.PnlWeight * normPnl
               + config.SharpeWeight * normSharpe
               + config.RrWeight * normRr
               - config.DrawdownPenaltyWeight * ddPenalty;
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

    private static string DominantTag(
        IReadOnlyList<BacktestTradeResult>? trades,
        Func<BacktestTradeResult, string?> selector,
        string fallback)
    {
        if (trades == null || trades.Count == 0) return fallback;
        var top = trades
            .Select(selector)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .GroupBy(t => t!)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        return top?.Key ?? fallback;
    }

    private static string ComputeResultHash(BacktestResult result)
    {
        var raw = $"{result.Symbol}|{result.TotalTrades}|{result.TotalPnL:F4}|{result.SharpeRatio:F6}|{result.MaxDrawdownPercent:F4}|{result.ProfitFactor:F4}";
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..12];
    }

    /// <summary>
    /// Maps an execution timeframe to its higher-timeframe counterpart used by the MTF
    /// confluence gate. Returns null when no reasonable HTF mapping exists (daily+).
    /// </summary>
    private static string? ResolveHigherTimeframe(string? timeframe) => timeframe?.Trim().ToLowerInvariant() switch
    {
        "1m" => "15m",
        "5m" => "1h",
        "15m" => "4h",
        "30m" => "4h",
        "1h" => "4h",
        "2h" => "1d",
        "4h" => "1d",
        _ => null
    };

    private static int EstimateExpectedCandleCount(DateTime start, DateTime end, string timeframe)
    {
        var minutes = Math.Max(1d, (end - start).TotalMinutes);
        var tfMinutes = timeframe?.Trim().ToLowerInvariant() switch
        {
            "1m" => 1d,
            "5m" => 5d,
            "15m" => 15d,
            "30m" => 30d,
            "1h" => 60d,
            "2h" => 120d,
            "4h" => 240d,
            "1d" => 1440d,
            _ => 60d
        };

        return Math.Max(1, (int)Math.Ceiling(minutes / tfMinutes));
    }

    private static DecisionCoverageFlags ResolveMissingCoverage(
        DecisionCoverageFlags observed,
        DecisionCoverageFlags required)
    {
        return required & ~observed;
    }

    private static bool EvaluateMlReadiness(BacktestResult result, BacktestParityOptions parityOptions)
    {
        if (!parityOptions.RequireMlModelAvailability)
        {
            return true;
        }

        if (!result.InferenceModelAvailable)
        {
            return false;
        }

        if (parityOptions.RequireInferencePredictionValidity && !result.InferencePredictionValid)
        {
            return false;
        }

        return true;
    }

    private static List<string> ComputeReadinessBlockers(
        BacktestResult result,
        PboResult? pbo,
        RollingWalkForwardGateResult? walkForward,
        int horizonDays,
        BacktestQualityGateOptions qualityGate,
        DecisionCoverageFlags missingCoverage,
        bool parityPassed,
        bool mlReadyPassed,
        BacktestParityOptions parityOptions)
    {
        var blockers = new List<string>();
        var expectancyPerTrade = result.TotalTrades > 0 ? result.TotalPnL / result.TotalTrades : 0m;

        if (result.TotalTrades < qualityGate.MinTrades) blockers.Add($"MinTrades<{qualityGate.MinTrades}");
        if (result.MaxDrawdownPercent > qualityGate.MaxDrawdownPercent) blockers.Add($"MaxDD>{qualityGate.MaxDrawdownPercent:F2}%");
        if (result.ProfitFactor < qualityGate.MinProfitFactor) blockers.Add($"ProfitFactor<{qualityGate.MinProfitFactor:F2}");
        if (result.WinRate < qualityGate.MinWinRatePercent) blockers.Add($"WinRate<{qualityGate.MinWinRatePercent:F1}%");
        if (expectancyPerTrade < qualityGate.MinExpectancyPerTrade) blockers.Add($"Expectancy<{qualityGate.MinExpectancyPerTrade:F2}");
        if (pbo != null && pbo.PBO > qualityGate.MaxPbo) blockers.Add($"PBO>{qualityGate.MaxPbo:F2}");
        if (walkForward is not null && walkForward.GateApplied)
        {
            if (!walkForward.Passed) blockers.Add("WalkForwardFailed");
            if (walkForward.CompletedWindows < qualityGate.MinWalkForwardWindows) blockers.Add($"WalkForwardWindows<{qualityGate.MinWalkForwardWindows}");
            if (walkForward.OosPositiveRatio < qualityGate.MinOosPositiveRatio) blockers.Add($"OosPositiveRatio<{qualityGate.MinOosPositiveRatio:F2}");
        }
        if (parityOptions.RequireParityCoverage && !parityPassed) blockers.Add($"CoverageGap:{missingCoverage}");
        if (parityOptions.RequireMlModelAvailability && !result.InferenceModelAvailable) blockers.Add("MlModelUnavailable");
        if (parityOptions.RequireInferencePredictionValidity && !result.InferencePredictionValid) blockers.Add("MlPredictionInvalid");
        if (parityOptions.RequireMlModelAvailability && !mlReadyPassed && result.InferenceModelAvailable) blockers.Add("MlReadinessFailed");

        var isShortHorizon = horizonDays <= qualityGate.ShortHorizonDays;
        if (isShortHorizon)
        {
            if (result.TotalPnL < qualityGate.ShortMinPnl) blockers.Add("ShortPnLBelowGate");
            if (result.SharpeRatio < qualityGate.ShortMinSharpe) blockers.Add("ShortSharpeBelowGate");
            if (result.AverageRR < qualityGate.ShortMinAverageRr) blockers.Add("ShortRrBelowGate");
        }
        else
        {
            if (result.TotalPnL < qualityGate.LongMinPnl) blockers.Add("LongPnLBelowGate");
            if (result.SharpeRatio < qualityGate.LongMinSharpe) blockers.Add("LongSharpeBelowGate");
            if (result.AverageRR < qualityGate.LongMinAverageRr) blockers.Add("LongRrBelowGate");
        }

        return blockers;
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
    public double WalkForwardEfficiency { get; init; }
    public bool WalkForwardPassed { get; init; }
    public int WalkForwardWindows { get; init; }
    public bool WalkForwardGateApplied { get; init; }
    public string WalkForwardGateReason { get; init; } = "";
    public string RiskProfile { get; init; } = "Moderate";
    public bool HardGatePassed { get; init; }
    public double ObjectiveScore { get; init; }
    public DecisionCoverageFlags CoverageFlags { get; init; }
    public string CoverageSummary { get; init; } = "";
    public Dictionary<string, int> EntryRejectReasons { get; init; } = new();
    public DecisionCoverageFlags MissingCoverageFlags { get; init; }
    public bool ParityPassed { get; init; }
    public bool InferenceModelAvailable { get; init; }
    public bool InferencePredictionValid { get; init; }
    public bool MlReadyPassed { get; init; }
    public List<string> ReadinessBlockers { get; init; } = new();
    public double WinRate { get; init; }
    public double AverageRR { get; init; }
    public int WinningTrades { get; init; }
    public int LosingTrades { get; init; }
    public decimal AverageWin { get; init; }
    public decimal AverageLoss { get; init; }
    public decimal ExpectancyPerTrade { get; init; }
    public int CandleCount { get; init; }
    public string TradeLogPath { get; init; } = "";
    public string EquityCurvePath { get; init; } = "";
    /// <summary>True when walk-forward grid search selected params applied for primary replay, PBO, and rolling WF gate.</summary>
    public bool WfoProfileApplied { get; init; }
    /// <summary>Human-readable WFO parameter triple when profile selection ran.</summary>
    public string WfoPrimaryParamsSummary { get; init; } = "";
    /// <summary>Aggregate WFO OOS robustness flag from the profile-selection pass.</summary>
    public bool WfoParamSelectionRobust { get; init; }

    public static BacktestSessionResult Skipped(string symbol, string version, string reason) => new()
    {
        Symbol = symbol,
        Version = version,
        Status = BacktestSessionStatus.Skipped,
        SkipReason = reason
    };
}

public record RollingWalkForwardGateResult
{
    public string Symbol { get; init; } = "";
    public int CompletedWindows { get; init; }
    public double WalkForwardEfficiency { get; init; }
    public double AggregateOosSharpe { get; init; }
    public decimal AggregateOosPnL { get; init; }
    public double OosPositiveRatio { get; init; }
    public bool Passed { get; init; }
    public bool GateApplied { get; init; }
    public string GateReason { get; init; } = "";

    public static RollingWalkForwardGateResult Empty(string symbol) => new()
    {
        Symbol = symbol,
        CompletedWindows = 0,
        WalkForwardEfficiency = 0,
        AggregateOosSharpe = 0,
        AggregateOosPnL = 0,
        OosPositiveRatio = 0,
        Passed = false,
        GateApplied = false,
        GateReason = "No walk-forward windows"
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
