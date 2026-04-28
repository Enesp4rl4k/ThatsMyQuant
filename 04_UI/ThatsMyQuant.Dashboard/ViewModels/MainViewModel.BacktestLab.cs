using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using ThatsMyQuant.Application.Strategy;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private int _totalBacktestTrades;
    private const int BacktestLogLimit = 500;
    private int CalculateRequiredCandleCount(DateTime start, DateTime end, string timeframe)
    {
        var duration = end - start;
        double minutes = duration.TotalMinutes;
        int interval = timeframe switch
        {
            "1m" => 1,
            "5m" => 5,
            "15m" => 15,
            "1h" => 60,
            "4h" => 240,
            "1d" => 1440,
            _ => 60
        };
        // Add 200 bars buffer for technical indicators
        return (int)Math.Ceiling(minutes / interval) + 200;
    }

    [RelayCommand]
    private async Task RunBacktestLab()
    {
        if (Studio.IsRunningBacktest) return;

        if (!ValidateBacktestInputs())
        {
            return;
        }

        Studio.IsRunningBacktest = true;
        Studio.BacktestProgress = 0;
        Studio.BacktestLogs.Clear();

        AddSystemLog($"Starting Lab Backtest for {MarketOverview.SelectedSymbol} using {Studio.SelectedBotStrategy}...");
        Studio.BacktestStatus = $"Simulating {MarketOverview.SelectedSymbol} ({MarketOverview.SelectedTimeframe})...";
        Studio.BacktestScenario = $"{MarketOverview.SelectedMarket} | {MarketOverview.SelectedSymbol} | {MarketOverview.SelectedTimeframe} | Strategy: {Studio.SelectedBotStrategy}";

        var rangeStart = Studio.BacktestStartDate.Date;
        var rangeEnd = Studio.BacktestEndDate.Date.AddDays(1).AddTicks(-1);
        int count = CalculateRequiredCandleCount(rangeStart, rangeEnd, MarketOverview.SelectedTimeframe);
        AppendBacktestLog($"[SYSTEM] Strategy: {Studio.SelectedBotStrategy}");
        AppendBacktestLog($"[SYSTEM] Fetching {count} candles for range {rangeStart:yyyy-MM-dd} to {rangeEnd:yyyy-MM-dd}");

        try
        {
            Studio.BacktestProgress = 10;
            var candles = await _marketDataService.GetCandlesAsync(
                MarketOverview.SelectedSymbol,
                count,
                MarketOverview.SelectedMarket,
                MarketOverview.SelectedTimeframe);

            Studio.BacktestProgress = 40;

            // Filter and sort
            candles = candles.Where(c => c.Timestamp >= rangeStart && c.Timestamp <= rangeEnd)
                             .OrderBy(c => c.Timestamp)
                             .ToList();

            if (candles.Count < 5)
            {
                Studio.BacktestStatus = "Insufficient data in the selected date range. Try widening dates or timeframe.";
                AppendBacktestLog("[SYSTEM] Backtest aborted because fewer than 5 candles were available.");
                return;
            }

            AppendBacktestLog($"[SYSTEM] Starting simulation loop on {candles.Count} data points...");
            Studio.BacktestProgress = 60;

            // Run engine
            var result = await _backtestEngine.RunAsync(candles, MarketOverview.SelectedSymbol, MarketOverview.SelectedTimeframe, Studio.SelectedRiskProfile, 100000);
            var session = await RunOrchestratorSessionAsync(MarketOverview.SelectedSymbol, rangeStart, rangeEnd);
            var snapshot = BuildBacktestLabSnapshot(MarketOverview.SelectedSymbol, result, session);
            UpdateWalkForwardUi(session);

            InvokeOnUi(() =>
            {
                ApplyBacktestResultsToUi(candles, result);
                UpdateBacktestLabResults(snapshot);
                UpdateReadinessDiagnostics(result, session);
                snapshot.PaperPromotionSummary = Studio.PaperPromotionGateSummary;
                Studio.BacktestProgress = 100;
                Studio.BacktestStatus = "Lab backtest completed.";
                AppendBacktestLog($"[SYSTEM] Lab Backtest Complete. Trades={result.TotalTrades}, PnL={result.TotalPnL:N2}");
                if (session is not null)
                {
                    AppendBacktestLog($"[GATE] WFE={session.WalkForwardEfficiency:F3}, WF windows={session.WalkForwardWindows}, WF pass={session.WalkForwardPassed}");
                    AppendBacktestLog($"[GATE] WF enforcement: {session.WalkForwardGateReason}");
                    AppendBacktestLog($"[GATE] PBO={session.PboScore:F3}, Overall pass={session.BacktestPass}");
                    if (!string.IsNullOrWhiteSpace(session.TradeLogPath))
                    {
                        AppendBacktestLog($"[REPORT] Trade CSV: {session.TradeLogPath}");
                    }
                    if (!string.IsNullOrWhiteSpace(session.EquityCurvePath))
                    {
                        AppendBacktestLog($"[REPORT] Equity CSV: {session.EquityCurvePath}");
                    }
                }
            });

            await PersistBacktestLabSnapshotAsync(snapshot);
        }
        catch (Exception ex)
        {
            AddSystemLog($"ERROR: Backtest failed: {ex.Message}");
            Studio.BacktestStatus = "Backtest failed. Check logs for details.";
            AppendBacktestLog($"[ERROR] {ex.Message}");
        }
        finally
        {
            Studio.IsRunningBacktest = false;
        }
    }

    [RelayCommand]
    private async Task RunMultiAssetBacktest()
    {
        if (!ValidateBacktestInputs())
        {
            return;
        }

        var selectedItems = WatchlistItems.Where(w => w.IsSelectedForBacktest).ToList();
        var symbolsToProcess = selectedItems.Any() ? selectedItems.Select(x => x.Symbol).ToList() : MarketOverview.Symbols.ToList();

        if (symbolsToProcess.Count == 0)
        {
            AddSystemLog("MULTI-BACKTEST: No symbols selected or available in market overview.");
            return;
        }

        if (Studio.IsRunningBacktest) return;
        Studio.IsRunningBacktest = true;
        Studio.BacktestProgress = 0;
        Studio.BacktestLogs.Clear();

        AddSystemLog($"MULTI-BACKTEST: Starting simulation for {symbolsToProcess.Count} assets using {Studio.SelectedBotStrategy}...");
        Studio.BacktestScenario = $"Multi-Asset Batch | {MarketOverview.SelectedTimeframe} | Strategy: {Studio.SelectedBotStrategy}";
        Studio.GlobalBacktestResults.Clear();

        var rangeStart = Studio.BacktestStartDate.Date;
        var rangeEnd = Studio.BacktestEndDate.Date.AddDays(1).AddTicks(-1);
        int processed = 0;
        int succeeded = 0;
        int skipped = 0;
        int failed = 0;
        var completedSessions = new List<BacktestSessionResult>();
        foreach (var symbol in symbolsToProcess)
        {
            try
            {
                Studio.BacktestStatus = $"Processing {symbol} ({processed + 1}/{symbolsToProcess.Count})...";
                AppendBacktestLog($"[BATCH] Initializing {symbol}...");

                int count = CalculateRequiredCandleCount(rangeStart, rangeEnd, MarketOverview.SelectedTimeframe);
                var candles = await _marketDataService.GetCandlesAsync(
                    symbol,
                    count,
                    MarketOverview.SelectedMarket,
                    MarketOverview.SelectedTimeframe);

                candles = candles.Where(c => c.Timestamp >= rangeStart && c.Timestamp <= rangeEnd)
                                 .OrderBy(c => c.Timestamp)
                                 .ToList();

                if (candles.Count >= 5)
                {
                    var result = await _backtestEngine.RunAsync(candles, symbol, MarketOverview.SelectedTimeframe, Studio.SelectedRiskProfile, 100000);
                    var session = await RunOrchestratorSessionAsync(symbol, rangeStart, rangeEnd);
                    var snapshot = BuildBacktestLabSnapshot(symbol, result, session);
                    UpdateWalkForwardUi(session);
                    
                    decimal returnPct = result.StartingCapital > 0
                        ? (result.FinalEquity - result.StartingCapital) / result.StartingCapital * 100
                        : 0;

                    var vm = new GlobalBacktestResultViewModel
                    {
                        Symbol = symbol,
                        TotalTrades = result.TotalTrades,
                        WinRate = result.WinRate,
                        TotalPnL = (decimal)result.TotalPnL,
                        ProfitFactor = (decimal)result.ProfitFactor,
                        SharpeRatio = result.SharpeRatio,
                        SortinoRatio = result.SortinoRatio,
                        MaxDrawdownPercent = result.MaxDrawdownPercent,
                        AverageRR = result.AverageRR,
                        StartingCapital = result.StartingCapital,
                        FinalEquity = result.FinalEquity,
                        ReturnPercent = $"{returnPct:F2}%",
                        PnLColor = result.TotalPnL >= 0 ? GreenBrush : RedBrush,
                        BacktestPass = result.TotalPnL > 0 && result.ProfitFactor > 1.1,
                        CostLeakagePercent = session?.TcaReport?.CostLeakagePercent ?? result.CostLeakagePercent,
                        GrossPnL = session?.TcaReport?.GrossPnL ?? result.GrossPnL,
                        TotalCost = (session?.TcaReport?.TotalFees ?? result.TotalFeesPaid) + (session?.TcaReport?.ImplicitSlippageCost ?? 0m),
                        Sharpe5Pct = session?.BootstrapReport?.Sharpe5Pct ?? 0,
                        Sharpe95Pct = session?.BootstrapReport?.Sharpe95Pct ?? 0,
                        ProbabilityOfLoss = session?.BootstrapReport?.ProbabilityOfLoss ?? 0,
                        FractionalKelly = session?.PortfolioRiskReport?.FractionalKelly ?? 0m,
                        Cvar95 = session?.PortfolioRiskReport?.HistoricalCVaR ?? 0m,
                        PromotionApproved = session?.PromotionDecision?.Approved ?? false,
                        PromotionSummary = session?.PromotionDecision is null
                            ? ""
                            : session.PromotionDecision.Approved
                                ? $"✓ {session.PromotionDecision.PassedCriteria.Count} criteria passed"
                                : $"✗ {string.Join(" ; ", session.PromotionDecision.FailedCriteria)}"
                    };

                    InvokeOnUi(() => 
                    {
                        Studio.GlobalBacktestResults.Add(vm);
                        UpdateBacktestLabResults(snapshot);
                        UpdateReadinessDiagnostics(result, session);
                        snapshot.PaperPromotionSummary = Studio.PaperPromotionGateSummary;
                    });
                    await PersistBacktestLabSnapshotAsync(snapshot);

                    succeeded++;
                    AppendBacktestLog($"[BATCH] {symbol} finished. PnL: {result.TotalPnL:N2} | Return: {returnPct:F2}%");
                    if (session is not null)
                    {
                        completedSessions.Add(session);
                        AppendBacktestLog($"[BATCH] {symbol} gates => WFE={session.WalkForwardEfficiency:F3} (pass={session.WalkForwardPassed}), PBO={session.PboScore:F3}, pass={session.BacktestPass}");
                        AppendBacktestLog($"[BATCH] {symbol} WF gate: {session.WalkForwardGateReason}");
                    }
                }
                else
                {
                    skipped++;
                    AppendBacktestLog($"[BATCH] {symbol} skipped (insufficient data).");
                }
            }
            catch (Exception ex)
            {
                failed++;
                AppendBacktestLog($"[BATCH] {symbol} error: {ex.Message}");
            }

            processed++;
            Studio.BacktestProgress = (double)processed / symbolsToProcess.Count * 100;
        }

        Studio.BacktestStatus = $"Multi-asset simulation complete. Success={succeeded}, Skipped={skipped}, Failed={failed}.";
        AddSystemLog($"MULTI-BACKTEST: All batches finalized. Success={succeeded}, Skipped={skipped}, Failed={failed}.");
        AppendBacktestLog($"[BATCH] Summary => Success={succeeded}, Skipped={skipped}, Failed={failed}");
        UpdatePortfolioValidationUi(completedSessions);
        Studio.IsRunningBacktest = false;
    }

    private bool ValidateBacktestInputs()
    {
        if (string.IsNullOrWhiteSpace(MarketOverview.SelectedSymbol))
        {
            Studio.BacktestStatus = "Select a symbol before running a backtest.";
            AddSystemLog("BACKTEST: No symbol selected.");
            return false;
        }

        if (Studio.BacktestEndDate.Date < Studio.BacktestStartDate.Date)
        {
            Studio.BacktestStatus = "Invalid date range. End date must be on or after start date.";
            AddSystemLog("BACKTEST: Invalid date range selected.");
            return false;
        }

        if ((Studio.BacktestEndDate.Date - Studio.BacktestStartDate.Date).TotalDays < 1)
        {
            Studio.BacktestStatus = "Date range is too short. Select at least a 1-day window for stable results.";
            AddSystemLog("BACKTEST: Date range too short.");
            return false;
        }

        return true;
    }

    private void AppendBacktestLog(string message)
    {
        Studio.BacktestLogs.Add($"{DateTime.Now:HH:mm:ss} | {message}");
        if (Studio.BacktestLogs.Count <= BacktestLogLimit)
        {
            return;
        }

        while (Studio.BacktestLogs.Count > BacktestLogLimit)
        {
            Studio.BacktestLogs.RemoveAt(0);
        }
    }

    private async Task<BacktestSessionResult?> RunOrchestratorSessionAsync(string symbol, DateTime rangeStart, DateTime rangeEnd)
    {
        try
        {
            var orchestrator = App.ServiceProvider?.GetService<BacktestOrchestrator>();
            if (orchestrator is null)
            {
                AppendBacktestLog("[GATE] BacktestOrchestrator not available; using engine-only metrics.");
                return null;
            }

            return await orchestrator.RunAutomatedSessionAsync(
                symbol,
                MarketOverview.SelectedTimeframe,
                rangeStart,
                rangeEnd,
                100000m,
                Studio.SelectedRiskProfile);
        }
        catch (Exception ex)
        {
            AppendBacktestLog($"[GATE] Orchestrator session failed: {ex.Message}");
            return null;
        }
    }

    private void UpdateWalkForwardUi(BacktestSessionResult? session)
    {
        if (session is null)
        {
            Studio.BacktestWalkForwardEfficiencyLabel = "WFE: --";
            Studio.BacktestWalkForwardWindowsLabel = "WF windows: --";
            Studio.BacktestWalkForwardPassLabel = "WF pass: --";
            Studio.BacktestWalkForwardPassBrush = GrayBrush;
            Studio.BacktestPromotionGateLabel = "Backtest gate: --";
            Studio.BacktestPromotionGateBrush = GrayBrush;
            Studio.PaperPromotionGateLabel = "Paper gate: PENDING";
            Studio.PaperPromotionGateBrush = GrayBrush;
            Studio.PaperPromotionGateSummary = "Run paper session after backtest PASS to unlock promotion.";
            Studio.BacktestReadinessBlockers = "Readiness blockers: --";
            Studio.PortfolioValidationGateLabel = "Portfolio gate: --";
            Studio.PortfolioValidationGateBrush = GrayBrush;
            Studio.PortfolioValidationSummary = "Portfolio validation is waiting for a multi-asset batch.";
            Studio.CostRealismGateLabel = "Cost gate: --";
            Studio.CostRealismGateBrush = GrayBrush;
            Studio.CostRealismSummary = "Cost realism is waiting for the next backtest session.";
            Studio.PaperDriftGateLabel = "Drift gate: --";
            Studio.PaperDriftGateBrush = GrayBrush;
            Studio.PaperDriftSummary = "Paper drift proxy is waiting for the next gate evaluation.";
            return;
        }

        Studio.BacktestWalkForwardEfficiencyLabel = $"WFE: {session.WalkForwardEfficiency:F3}";
        Studio.BacktestWalkForwardWindowsLabel = $"WF windows: {session.WalkForwardWindows}";
        Studio.BacktestWalkForwardPassLabel = $"WF pass: {(session.WalkForwardPassed ? "PASS" : "FAIL")}";
        Studio.BacktestWalkForwardPassBrush = session.WalkForwardPassed ? GreenBrush : RedBrush;
        Studio.BacktestPromotionGateLabel = $"Backtest gate: {(session.BacktestPass ? "PASS" : "FAIL")}";
        Studio.BacktestPromotionGateBrush = session.BacktestPass ? GreenBrush : RedBrush;
        RefreshPaperPromotionSummary(session);
        Studio.BacktestReadinessBlockers = session.ReadinessBlockers.Count > 0
            ? $"Readiness blockers: {string.Join(", ", session.ReadinessBlockers)}"
            : "Readiness blockers: none";
    }

    private void UpdatePortfolioValidationUi(IReadOnlyList<BacktestSessionResult> sessions)
    {
        if (sessions.Count == 0)
        {
            Studio.PortfolioValidationGateLabel = "Portfolio gate: --";
            Studio.PortfolioValidationGateBrush = GrayBrush;
            Studio.PortfolioValidationSummary = "Portfolio validation is waiting for a multi-asset batch.";
            return;
        }

        var validation = BacktestOrchestrator.EvaluatePortfolioValidation(sessions);
        var weakest = validation.WeakestSession;

        Studio.PortfolioValidationGateLabel = validation.IsReady ? "Portfolio gate: READY" : "Portfolio gate: BLOCKED";
        Studio.PortfolioValidationGateBrush = validation.IsReady ? GreenBrush : GrayBrush;
        Studio.PortfolioValidationSummary =
            $"{validation.PassedSymbols}/{validation.TotalSymbols} symbols cleared the acceptance gate. " +
            $"Pooled PnL {validation.PooledPnL:N2}, avg Sharpe {validation.AverageSharpe:F2}, avg PF {validation.AverageProfitFactor:F2}, avg expectancy {validation.AverageExpectancyPerTrade:N2}. " +
            $"Weakest bucket: {weakest?.Symbol ?? "--"} {(weakest?.ReadinessBlockers.Count > 0 ? $"[{string.Join(", ", weakest.ReadinessBlockers)}]" : "[none]")}.";
    }

    private (bool IsReady, string Summary) EvaluatePaperPromotionGate(
        BacktestSessionResult session,
        bool costGatePass,
        bool driftGatePass)
    {
        var options = App.ServiceProvider?.GetService<IOptions<ProfitMaximizationOptions>>()?.Value ?? new ProfitMaximizationOptions();
        var config = options.Resolve(Studio.SelectedRiskProfile);
        var minSharpe = config.MinSharpe;
        var minWinRate = config.MinWinRatePercent;
        var minRr = config.MinAverageRr;
        var expectancy = session.TotalTrades > 0 ? session.TotalPnL / session.TotalTrades : 0m;

        var ready = session.BacktestPass
            && session.ParityPassed
            && session.MlReadyPassed
            && session.SharpeRatio >= minSharpe
            && session.WinRate >= minWinRate
            && session.AverageRR >= minRr
            && expectancy >= config.MinExpectancy
            && session.MaxDrawdownPercent <= config.MaxDailyDrawdownPercent
            && costGatePass
            && driftGatePass;

        if (!ready)
        {
            var blockers = new List<string>();
            if (session.ReadinessBlockers.Count > 0)
            {
                blockers.AddRange(session.ReadinessBlockers);
            }

            if (!costGatePass)
            {
                blockers.Add("CostGate");
            }

            if (!driftGatePass)
            {
                blockers.Add("DriftGate");
            }

            var blockerSummary = blockers.Count > 0 ? string.Join(", ", blockers.Distinct()) : "Metric gates not satisfied";
            return (false, $"Paper blocked: {blockerSummary}. Profile={Studio.SelectedRiskProfile} still needs Parity=PASS, ML=READY, Sharpe>={minSharpe:F2}, WinRate>={minWinRate:F1}%, R:R>={minRr:F2}, Expectancy>={config.MinExpectancy:N2}, DD<={config.MaxDailyDrawdownPercent:F2}%.");
        }

        return (true, $"Paper ready: backtest, cost realism, and pre-paper drift gates are aligned for {Studio.SelectedRiskProfile}. Run 7-14d paper validation and watch slippage drift.");
    }

    private void UpdateReadinessDiagnostics(BacktestResult result, BacktestSessionResult? session)
    {
        var totalFees = result.Trades.Sum(t => t.TotalFeesPaid);
        var totalFunding = result.Trades.Sum(t => t.NetFunding);
        var avgFees = result.TotalTrades > 0 ? totalFees / result.TotalTrades : 0m;
        var avgNetPnl = result.TotalTrades > 0 ? result.TotalPnL / result.TotalTrades : 0m;
        var feeDragRatio = result.TotalPnL != 0m ? totalFees / Math.Max(Math.Abs(result.TotalPnL), 1m) : totalFees;
        var costGatePass = result.TotalTrades > 0
            && avgNetPnl > 0m
            && feeDragRatio <= 0.35m;
        var costBlocker = result.TotalTrades == 0
            ? "waiting for filled trades"
            : avgNetPnl <= 0m
                ? "net expectancy is non-positive after costs"
                : feeDragRatio > 0.35m
                    ? $"fee drag too heavy ({feeDragRatio:P0} of net PnL)"
                    : "cost drag within tolerance";
        Studio.CostRealismGateLabel = costGatePass ? "Cost gate: PASS" : "Cost gate: BLOCKED";
        Studio.CostRealismGateBrush = costGatePass ? GreenBrush : GrayBrush;
        Studio.CostRealismSummary =
            $"Cost realism: total fees {totalFees:N2}, funding {totalFunding:N2}, avg fee/trade {avgFees:N2}, avg net PnL/trade {avgNetPnl:N2}. Blocker: {costBlocker}.";

        if (session is null)
        {
            Studio.PaperDriftGateLabel = "Drift gate: --";
            Studio.PaperDriftGateBrush = GrayBrush;
            Studio.PaperDriftSummary = "Paper drift proxy is waiting for orchestrator gate output.";
            return;
        }

        var driftGatePass = session.WalkForwardPassed
            && session.WalkForwardWindows >= 3
            && session.PboScore >= 0
            && session.PboScore <= 0.35;
        var driftStance = driftGatePass
            ? "stable"
            : "fragile";
        var driftBlocker = !session.WalkForwardPassed
            ? "walk-forward gate failed"
            : session.WalkForwardWindows < 3
                ? $"too few WF windows ({session.WalkForwardWindows})"
                : session.PboScore < 0
                    ? "PBO not computed"
                    : session.PboScore > 0.35
                        ? $"PBO too high ({session.PboScore:F3})"
                        : "drift pressure within tolerance";
        Studio.PaperDriftGateLabel = driftGatePass ? "Drift gate: PASS" : "Drift gate: BLOCKED";
        Studio.PaperDriftGateBrush = driftGatePass ? GreenBrush : GrayBrush;
        Studio.PaperDriftSummary =
            $"Pre-paper drift proxy: WF {(session.WalkForwardPassed ? "PASS" : "FAIL")} over {session.WalkForwardWindows} windows, PBO {session.PboScore:F3}, stance {driftStance}. Blocker: {driftBlocker}.";
        RefreshPaperPromotionSummary(session);
    }

    private void RefreshPaperPromotionSummary(BacktestSessionResult session)
    {
        var costGatePass = Studio.CostRealismGateLabel.Contains("PASS", StringComparison.OrdinalIgnoreCase);
        var driftGatePass = Studio.PaperDriftGateLabel.Contains("PASS", StringComparison.OrdinalIgnoreCase);
        var (paperReady, summary) = EvaluatePaperPromotionGate(session, costGatePass, driftGatePass);
        Studio.PaperPromotionGateLabel = paperReady ? "Paper gate: READY" : "Paper gate: BLOCKED";
        Studio.PaperPromotionGateBrush = paperReady ? OrangeBrush : GrayBrush;
        Studio.PaperPromotionGateSummary = summary;
    }

    private StrategyBucketPerformanceSnapshot BuildBacktestLabSnapshot(string symbol, BacktestResult result, BacktestSessionResult? session = null)
    {
        var winRate = result.WinRate;
        var enginePass = result.TotalTrades >= 30 &&
                   result.TotalPnL > 0 &&
                   result.ProfitFactor >= 1.2 &&
                   result.SharpeRatio >= 0.5 &&
                   result.MaxDrawdownPercent <= 25m;
        var pass = session?.BacktestPass ?? enginePass;
        var version = $"lab_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        var hash = ComputeBacktestHash(result);
        return new StrategyBucketPerformanceSnapshot
        {
            Symbol = symbol,
            StrategyTag = Studio.SelectedBotStrategy,
            SessionTag = $"BacktestLab_{DateTime.UtcNow:yyyyMMdd_HHmm}",
            Trades = result.TotalTrades,
            WinRate = winRate,
            ProfitFactor = result.ProfitFactor,
            ExpectedValue = session?.ExpectancyPerTrade ?? (result.TotalTrades > 0 ? result.TotalPnL / result.TotalTrades : 0m),
            TotalPnL = (decimal)result.TotalPnL,
            SharpeRatio = result.SharpeRatio,
            SortinoRatio = result.SortinoRatio,
            MaxDrawdownPercent = (double)result.MaxDrawdownPercent,
            AverageRR = result.AverageRR,
            StartingCapital = result.StartingCapital,
            FinalEquity = result.FinalEquity,
            BacktestVersion = version,
            BacktestHash = hash,
            BacktestPass = pass,
            PromotionStage = "Backtest Lab",
            PaperPromotionSummary = session is null ? "Paper readiness summary pending orchestrator diagnostics." : "Paper readiness summary pending UI diagnostics.",
            RecordedAtUtc = DateTime.UtcNow
        };
    }

    private void UpdateBacktestLabResults(StrategyBucketPerformanceSnapshot snapshot)
    {
        var existing = BacktestBuckets.FirstOrDefault(b => b.Symbol == snapshot.Symbol);
        if (existing != null) BacktestBuckets.Remove(existing);
        
        BacktestBuckets.Add(snapshot);
        
        // Refresh aggregate metrics
        TopProfitFactor = BacktestBuckets.Count > 0 ? BacktestBuckets.Max(b => b.ProfitFactor) : 0;
        TotalBacktestTrades = BacktestBuckets.Count > 0 ? BacktestBuckets.Sum(b => b.Trades) : 0;
        CumulativePnL = BacktestBuckets.Count > 0 ? BacktestBuckets.Sum(b => b.TotalPnL) : 0;
        PromotionReadyCount = BacktestBuckets.Count(b => b.BacktestPass);
        BestSharpeRatio = BacktestBuckets.Count > 0 ? BacktestBuckets.Max(b => b.SharpeRatio) : 0;
        BestSortinoRatio = BacktestBuckets.Count > 0 ? BacktestBuckets.Max(b => b.SortinoRatio) : 0;
        AvgMaxDrawdown = BacktestBuckets.Count > 0 ? BacktestBuckets.Average(b => b.MaxDrawdownPercent) : 0;
        AvgWinRate = BacktestBuckets.Count > 0 ? BacktestBuckets.Average(b => b.WinRate) : 0;
    }

    private async Task PersistBacktestLabSnapshotAsync(StrategyBucketPerformanceSnapshot snapshot)
    {
        try
        {
            var state = await _performanceStore.LoadAsync();
            state.BacktestBuckets.Add(snapshot);
            state.RecordedAtUtc = DateTime.UtcNow;
            await _performanceStore.SaveAsync(state);
            AppendBacktestLog($"[STORE] Persisted {snapshot.Symbol} v={snapshot.BacktestVersion} pass={snapshot.BacktestPass}");
        }
        catch (Exception ex)
        {
            AppendBacktestLog($"[STORE] Persistence failed for {snapshot.Symbol}: {ex.Message}");
        }
    }

    private static string ComputeBacktestHash(BacktestResult result)
    {
        var raw = $"{result.Symbol}|{result.TotalTrades}|{result.TotalPnL:F4}|{result.SharpeRatio:F6}|{result.MaxDrawdownPercent:F4}|{result.ProfitFactor:F4}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..12];
    }
}
