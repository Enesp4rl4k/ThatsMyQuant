using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Dashboard.Services;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class MainViewModel
{
    private async Task RefreshQueueStats()
    {
        try
        {
            var counts = await _signalRepository.GetStatusCountsAsync();

            InvokeOnUi(() =>
            {
                QueuedCount = counts.GetValueOrDefault(TradeStatus.Queued, 0);
                ExecutedCount = counts.GetValueOrDefault(TradeStatus.Executed, 0);
                FailedCount = counts.GetValueOrDefault(TradeStatus.Failed, 0);
                DeadLetterCount = counts.GetValueOrDefault(TradeStatus.DeadLetter, 0);

                if (QueuedCount > 0 || ExecutedCount > 0)
                {
                    AddSystemLog($"TELEMETRY: Signals Queued: {QueuedCount}, Executed: {ExecutedCount}");
                }

                if (DeadLetterCount > 0) QueueHealth = "Critical (Dead-Letter)";
                else if (QueuedCount > 5) QueueHealth = "Congested";
                else QueueHealth = "Optimal";

                UpdateHftWorkspace();
            });
        }
        catch (Exception ex)
        {
            AddSystemLog($"Queue Telemetry Error: {ex.Message}");
        }
    }

    private async Task RefreshStrategyLeaderboard()
    {
        try
        {
            var recentTrades = await _tradeRepository.GetAllAsync(250);
            var recentSignals = await _signalRepository.GetRecentAsync(250);

            InvokeOnUi(() =>
            {
                StrategyLeaderboard.Clear();

                var tradeRows = recentTrades
                    .Where(t => t.Status == TradeStatus.Executed || t.Status == TradeStatus.Failed)
                    .ToList();

                if (tradeRows.Count == 0 && recentTrades.Count == 0)
                {
                    HandleEmptyLeaderboard();
                    return;
                }
                
                RefreshOpenPositionsFromTrades(recentTrades);

                _tradeCache.Clear();
                _tradeCache.AddRange(recentTrades);
                _signalCache.Clear();
                _signalCache.AddRange(recentSignals);
                RefreshTradeJournalFromTrades(recentTrades);
                RefreshOpenPositionsFromTrades(recentTrades);
                UpdatePortfolioGrowth();
                RefreshRiskPanel();

                var rows = tradeRows
                    .GroupBy(CreateLeaderboardBucketKey)
                    .Select(ProjectLeaderboardRow)
                    .OrderByDescending(r => r.ProfitFactor)
                    .ThenByDescending(r => r.TotalPnL)
                    .Take(6)
                    .ToList();

                foreach (var row in rows)
                {
                    StrategyLeaderboard.Add(row);
                }

                UpdateStrategyHealthSnapshots(rows);

                _ = PersistStrategyHealth();

                RefreshSessionPerformance(tradeRows);
                RefreshEdgeAnalytics(tradeRows);
                RefreshModelGovernanceAnalytics();
                RefreshCapitalAllocation();
                RefreshStrategyTuningStudio();

                UpdateLeaderboardOverallStatus(rows, tradeRows);
                EvaluateStrategyThrottle();
                UpdateBotLaunchSummary();
                UpdateSessionSummary();
            });
        }
        catch (Exception ex)
        {
            HandleLeaderboardError(ex);
        }
    }

    private void HandleEmptyLeaderboard()
    {
        StrategyLeaderboardStatus = "No executed trades yet.";
        _tradeCache.Clear();
        _signalCache.Clear();
        RefreshOpenPositionsFromTrades(Array.Empty<Trade>());
        UpdatePortfolioGrowth();
        RefreshRiskPanel();
        RefreshEdgeAnalytics(Array.Empty<Trade>());
        RefreshModelGovernanceAnalytics();
        RefreshStrategyTuningStudio();
        StrategyThrottleStatus = "Unrated";
        StrategyThrottleReason = "No strategy bucket has enough trade history yet.";
        StrategyThrottleColor = GrayBrush;
        RefreshCapitalAllocation();
    }

    private void HandleLeaderboardError(Exception ex)
    {
        InvokeOnUi(() =>
        {
            StrategyLeaderboardStatus = "Strategy analytics unavailable.";
            RefreshOpenPositionsFromTrades(Array.Empty<Trade>());
            UpdatePortfolioGrowth();
            RefreshSessionPerformance(Array.Empty<Trade>());
            RefreshEdgeAnalytics(Array.Empty<Trade>());
            RefreshModelGovernanceAnalytics();
            RefreshDisabledStrategiesFromHealth();
            RefreshCapitalAllocation();
            StrategyThrottleStatus = "Unavailable";
            StrategyThrottleReason = "Strategy throttling is waiting for analytics to recover.";
            StrategyThrottleColor = OrangeBrush;
            UpdateBotLaunchSummary();
            AddSystemLog($"Strategy analytics error: {ex.Message}");
        });
    }

    private StrategyLeaderboardRow ProjectLeaderboardRow(IGrouping<StrategyBucketKey, Trade> group)
    {
        var pnls = group.Select(CalculateTradePnL).ToList();
        var wins = pnls.Count(p => p > 0);
        var grossProfit = pnls.Where(p => p > 0).Sum();
        var grossLoss = Math.Abs(pnls.Where(p => p < 0).Sum());
        var (averageVenueBias, venueImpact, venueSummary, venueImpactColor) = BuildStrategyVenueImpact(group);
        var (exactBucketImpact, exactBucketSummary, exactBucketImpactColor) = BuildExactBucketPressureDisplay(group);
        var (concordanceImpact, concordanceSummary, concordanceImpactColor) =
            BuildConcordanceDisplay(exactBucketImpact, exactBucketSummary, averageVenueBias, venueSummary);

        return new StrategyLeaderboardRow
        {
            StrategyTag = group.Key.StrategyTag,
            RegimeTag = group.Key.RegimeTag,
            ExecutionMode = ResolveExecutionModeScope(group),
            PromotionStage = ResolvePromotionStageScope(group),
            Trades = group.Count(),
            WinRate = group.Count() > 0 ? wins * 100.0 / group.Count() : 0,
            TotalPnL = pnls.Sum(),
            ProfitFactor = (double)CalculateProfitFactor(grossProfit, grossLoss),
            AverageVenueExecutionBias = averageVenueBias,
            VenuePromotionImpact = venueImpact,
            VenueExecutionSummary = venueSummary,
            ExactBucketPromotionImpact = exactBucketImpact,
            ExactBucketSummary = exactBucketSummary,
            ConcordanceImpact = concordanceImpact,
            ConcordanceSummary = concordanceSummary,
            ExactBucketPromotionImpactColor = exactBucketImpactColor,
            VenuePromotionImpactColor = venueImpactColor,
            ConcordanceImpactColor = concordanceImpactColor,
            PnlColor = pnls.Sum() >= 0 ? GreenBrush : RedBrush
        };
    }

    private static StrategyBucketKey CreateLeaderboardBucketKey(Trade trade) => CreateCrossSessionBucketKey(trade);

    private void UpdateStrategyHealthSnapshots(List<StrategyLeaderboardRow> rows)
    {
        var strategyOverrides = _persistedStrategyHealth
            .GroupBy(snapshot => NormalizeStrategyKey(snapshot.StrategyTag))
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    ManualDisabled = group.Any(item => item.ManuallyDisabled),
                    ForceEnabled = group.Any(item => item.ForceEnabled)
                });

        _persistedStrategyHealth.Clear();
        foreach (var row in rows)
        {
            strategyOverrides.TryGetValue(NormalizeStrategyKey(row.StrategyTag), out var flags);
            _persistedStrategyHealth.Add(new StrategyHealthSnapshot
            {
                StrategyTag = row.StrategyTag,
                RegimeTag = row.RegimeTag,
                Trades = row.Trades,
                WinRate = row.WinRate,
                ProfitFactor = row.ProfitFactor,
                TotalPnL = row.TotalPnL,
                ManuallyDisabled = flags?.ManualDisabled ?? false,
                ForceEnabled = flags?.ForceEnabled ?? false,
                RecordedAtUtc = DateTime.UtcNow
            });
        }

        foreach (var manualOnly in strategyOverrides
                     .Where(pair => pair.Value.ManualDisabled || pair.Value.ForceEnabled)
                     .Where(pair => _persistedStrategyHealth.All(s => NormalizeStrategyKey(s.StrategyTag) != pair.Key)))
        {
            _persistedStrategyHealth.Add(new StrategyHealthSnapshot
            {
                StrategyTag = manualOnly.Key,
                RegimeTag = "ManualOverride",
                ManuallyDisabled = manualOnly.Value.ManualDisabled,
                ForceEnabled = manualOnly.Value.ForceEnabled,
                RecordedAtUtc = DateTime.UtcNow
            });
        }
    }

    private void UpdateLeaderboardOverallStatus(List<StrategyLeaderboardRow> rows, List<Trade> tradeRows)
    {
        var dominantStage = rows
            .GroupBy(row => string.IsNullOrWhiteSpace(row.PromotionStage) ? "UNKNOWN" : row.PromotionStage)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault() ?? "UNKNOWN";

        StrategyLeaderboardStatus =
            $"Tracking {rows.Count} strategy buckets from {tradeRows.Count} trades. Dominant ladder stage: {dominantStage}. {ResolveStoredPromotionStageContext(tradeRows, dominantStage)}. {MlRetrainingTriggerSummary}";
    }

    private void RefreshModelGovernanceAnalytics()
    {
        ModelPerformanceRows.Clear();

        var snapshots = _modelGovernance.Analyze(_signalCache, _tradeCache);
        foreach (var snapshot in snapshots.Take(6))
        {
            ModelPerformanceRows.Add(new ModelPerformanceRow
            {
                ModelVersion = snapshot.ModelVersion,
                Signals = snapshot.SignalCount,
                ClosedTrades = snapshot.ClosedTrades,
                WinRate = snapshot.WinRate,
                ExpectedValue = snapshot.ExpectedValue,
                DriftRate = snapshot.DriftRate,
                VetoRate = snapshot.VetoRate,
                SizeTrimRate = snapshot.SizeTrimRate,
                Summary =
                    $"Signals {snapshot.SignalCount}, closed trades {snapshot.ClosedTrades}, drift {snapshot.DriftRate:F1}%, veto {snapshot.VetoRate:F1}%, size trim {snapshot.SizeTrimRate:F1}%, EV {snapshot.ExpectedValue:F2}.",
                StatusColor = snapshot.VetoRate >= 15 || snapshot.DriftRate >= 35
                    ? RedBrush
                    : snapshot.SizeTrimRate >= 15
                        ? OrangeBrush
                        : GreenBrush
            });
        }

        if (ModelPerformanceRows.Count == 0)
        {
            MlModelAnalyticsSummary = "Model governance is waiting for versioned signals.";
            MlRetrainingTriggerSummary = "Retraining triggers are quiet because no model-version history exists yet.";
            return;
        }

        var lead = ModelPerformanceRows[0];
        MlModelAnalyticsSummary =
            $"Per-model analytics are tracking {ModelPerformanceRows.Count} versions. Lead model {lead.ModelVersion} shows WR {lead.WinRate:F1}%, EV {lead.ExpectedValue:F2}, drift {lead.DriftRate:F1}%, veto {lead.VetoRate:F1}% and size trim {lead.SizeTrimRate:F1}%.";

        var trigger = _modelGovernance.EvaluateRetrainingNeed(_signalCache, _tradeCache);
        MlRetrainingTriggerSummary = trigger.Summary;
    }

    [RelayCommand]
    private async Task RefreshBacktest()
    {
        try
        {
            var state = await _performanceStore.LoadAsync();
            InvokeOnUi(() =>
            {
                BacktestBuckets.Clear();
                foreach (var s in state.BacktestBuckets.OrderByDescending(x => x.RecordedAtUtc))
                {
                    BacktestBuckets.Add(s);
                }

                if (BacktestBuckets.Count > 0)
                {
                    TopProfitFactor = BacktestBuckets.Max(x => x.ProfitFactor);
                    CumulativePnL = BacktestBuckets.Sum(x => x.TotalPnL);
                    PromotionReadyCount = BacktestBuckets.Count(x => x.BacktestPass);
                    BestSharpeRatio = BacktestBuckets.Max(x => x.SharpeRatio);
                    BestSortinoRatio = BacktestBuckets.Max(x => x.SortinoRatio);
                    AvgMaxDrawdown = BacktestBuckets.Average(x => x.MaxDrawdownPercent);
                    AvgWinRate = BacktestBuckets.Average(x => x.WinRate);
                }

                AddSystemLog($"Backtest: {BacktestBuckets.Count} performance snapshots synchronized.");
            });
        }
        catch (Exception ex)
        {
            AddSystemLog($"Backtest Load Error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ExportBacktestCsv()
    {
        try
        {
            if (BacktestBuckets.Count == 0)
            {
                AddSystemLog("Export: No backtest data to export.");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV Files (*.csv)|*.csv",
                DefaultExt = ".csv",
                FileName = $"backtest_report_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (dialog.ShowDialog() != true) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Timestamp,Symbol,Strategy,Regime,Trades,WinRate,ProfitFactor,NetPnL,StartingCapital,FinalEquity,SharpeRatio,SortinoRatio,MaxDrawdownPct,AvgRR,MaxConsecLosses,PboScore,StochasticDominance,PromotionStage,BacktestPass");

            foreach (var b in BacktestBuckets)
            {
                sb.AppendLine(string.Join(",",
                    b.RecordedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                    b.Symbol,
                    b.StrategyTag,
                    b.RegimeTag,
                    b.Trades,
                    b.WinRate.ToString("F4"),
                    b.ProfitFactor.ToString("F2"),
                    b.TotalPnL.ToString("F2"),
                    b.StartingCapital.ToString("F2"),
                    b.FinalEquity.ToString("F2"),
                    b.SharpeRatio.ToString("F4"),
                    b.SortinoRatio.ToString("F4"),
                    b.MaxDrawdownPercent.ToString("F2"),
                    b.AverageRR.ToString("F2"),
                    b.MaxConsecutiveLosses,
                    b.PboScore.ToString("F4"),
                    b.StochasticDominance.ToString("F4"),
                    b.PromotionStage,
                    b.BacktestPass));
            }

            await System.IO.File.WriteAllTextAsync(dialog.FileName, sb.ToString());
            AddSystemLog($"Export: Backtest report saved to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            AddSystemLog($"Export Error: {ex.Message}");
        }
    }
}
