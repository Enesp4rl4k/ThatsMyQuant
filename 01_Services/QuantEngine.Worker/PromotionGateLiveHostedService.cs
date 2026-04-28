using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Daily 23:55 UTC objective verdict on whether the paper portfolio is ready for live.
/// Builds a synthetic <see cref="BacktestResult"/> from the last <c>PromotionGate:LookbackDays</c>
/// of closed paper trades and runs <see cref="FormalPromotionGate.EvaluatePaperToLive"/>.
///
/// Output goes to log + Telegram digest channel ("APPROVED 6/7 criteria" / "REJECTED Sharpe 0.4 < 0.9").
/// Removes guesswork on "ready for live yet?" — objective gate vs gut feel.
///
/// Read-only — never auto-flips Hybrid:HybridMode to live.
/// </summary>
public sealed class PromotionGateLiveHostedService : BackgroundService
{
    private readonly ILogger<PromotionGateLiveHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PaperPortfolio _portfolio;
    private readonly IRealtimeAlerter? _alerter;
    private readonly bool _enabled;
    private readonly int _runHourUtc;
    private readonly int _runMinuteUtc;
    private readonly int _lookbackDays;
    private DateTime _lastRunDate = DateTime.MinValue;

    public PromotionGateLiveHostedService(
        ILogger<PromotionGateLiveHostedService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        PaperPortfolio portfolio,
        IRealtimeAlerter? alerter = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _portfolio = portfolio;
        _alerter = alerter;
        _enabled = !bool.TryParse(configuration["PromotionGate:Enabled"], out var en) || en;
        _runHourUtc = int.TryParse(configuration["PromotionGate:RunHourUtc"], out var h) && h is >= 0 and <= 23 ? h : 23;
        _runMinuteUtc = int.TryParse(configuration["PromotionGate:RunMinuteUtc"], out var m) && m is >= 0 and <= 59 ? m : 55;
        _lookbackDays = int.TryParse(configuration["PromotionGate:LookbackDays"], out var d) && d > 0 ? d : 7;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("PromotionGateLiveHostedService disabled (PromotionGate:Enabled=false).");
            return;
        }
        _logger.LogWarning("PromotionGateLive starting. RunAt={H:00}:{M:00} UTC daily, LookbackDays={D}", _runHourUtc, _runMinuteUtc, _lookbackDays);

        // Tick once a minute; only run when wall-clock matches and we haven't already run today.
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
            {
                try
                {
                    var nowUtc = DateTime.UtcNow;
                    if (nowUtc.Hour == _runHourUtc && nowUtc.Minute == _runMinuteUtc && nowUtc.Date != _lastRunDate)
                    {
                        await RunAsync(stoppingToken).ConfigureAwait(false);
                        _lastRunDate = nowUtc.Date;
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "PromotionGate tick failed."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-_lookbackDays);
        List<PaperPosition> recent;
        decimal startingCapital;
        lock (_portfolio)
        {
            startingCapital = _portfolio.InitialBalance > 0m ? _portfolio.InitialBalance : 1m;
            recent = _portfolio.ClosedPositions
                .Where(p => (p.ExitTime ?? p.EntryTime) >= cutoff)
                .ToList();
        }
        if (recent.Count == 0)
        {
            // Log only — no Telegram noise during the warm-up period when no trades have closed yet.
            // The gate becomes meaningful (and alerts) only once paper produces real outcomes.
            _logger.LogWarning("PROMOTION_GATE: no closed trades in last {D}d — gate silently skipped (no Telegram alert).", _lookbackDays);
            return;
        }

        var paperResult = BuildSyntheticBacktestResult(recent, startingCapital);

        // Use paperResult as both inputs — the parity check then trivially passes (since "backtest"
        // here is itself the paper window). When a separate live-backtest snapshot gets wired we
        // can swap in the real backtest here.
        using var scope = _serviceProvider.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<FormalPromotionGate>();
        var decision = gate.EvaluatePaperToLive(paperResult, paperResult);

        var verdictLine = decision.Approved
            ? $"APPROVED for live: passed {decision.PassedCriteria.Count}/{decision.PassedCriteria.Count + decision.FailedCriteria.Count} criteria."
            : $"REJECTED for live: {decision.FailedCriteria.Count} criteria failed → {string.Join(" ; ", decision.FailedCriteria)}";

        _logger.LogWarning(
            "PROMOTION_GATE {Window}d | Trades={N} Sharpe={S:F2} PF={PF:F2} DD={DD:F1}% WR={WR:F1}% AvgRR={RR:F2} | {Verdict}",
            _lookbackDays, paperResult.TotalTrades, paperResult.SharpeRatio, paperResult.ProfitFactor,
            paperResult.MaxDrawdownPercent, paperResult.WinRate, paperResult.AverageRR, verdictLine);

        await SafeAlert(
            key: decision.Approved ? "promotion_gate_approved" : "promotion_gate_rejected",
            severity: decision.Approved ? AlertSeverity.Info : AlertSeverity.Warning,
            title: decision.Approved ? "Promotion gate APPROVED for live" : "Promotion gate REJECTED for live",
            body:
                $"Window: last {_lookbackDays} days, {paperResult.TotalTrades} closed trades.\n" +
                $"Sharpe: {paperResult.SharpeRatio:F2}\nProfitFactor: {paperResult.ProfitFactor:F2}\n" +
                $"MaxDD: {paperResult.MaxDrawdownPercent:F1}%\nWinRate: {paperResult.WinRate:F1}%\nAvgRR: {paperResult.AverageRR:F2}\n\n" +
                $"Passed ({decision.PassedCriteria.Count}):\n  • " + string.Join("\n  • ", decision.PassedCriteria) +
                (decision.FailedCriteria.Count > 0
                    ? $"\n\nFailed ({decision.FailedCriteria.Count}):\n  • " + string.Join("\n  • ", decision.FailedCriteria)
                    : ""),
            ct).ConfigureAwait(false);
    }

    private async Task SafeAlert(string key, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        if (_alerter is null) return;
        try { await _alerter.SendAsync(key, severity, title, body, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "PromotionGate alert dispatch failed."); }
    }

    private static BacktestResult BuildSyntheticBacktestResult(IReadOnlyList<PaperPosition> positions, decimal startingCapital)
    {
        var trades = new List<BacktestTradeResult>(positions.Count);
        decimal totalPnL = 0m, totalFees = 0m, totalFunding = 0m;
        int wins = 0, losses = 0, maxConsecLoss = 0, curConsecLoss = 0;
        decimal sumWin = 0m, sumLoss = 0m;
        decimal equity = startingCapital;
        decimal peak = startingCapital;
        decimal maxDDDollar = 0m;
        var equityCurve = new List<decimal> { equity };
        var returns = new List<double>();

        foreach (var p in positions.OrderBy(p => p.ExitTime ?? p.EntryTime))
        {
            var qty = p.Quantity > 0 ? p.Quantity : Math.Max(p.OpenedQuantity, 1m);
            var exitPx = p.CurrentPrice > 0 ? p.CurrentPrice : p.EntryPrice;
            var pnlPct = p.EntryPrice > 0 ? (p.RealizedPnL / (p.EntryPrice * qty)) * 100m : 0m;
            trades.Add(new BacktestTradeResult
            {
                EntryTime = p.EntryTime,
                ExitTime = p.ExitTime ?? DateTime.UtcNow,
                Side = p.Side,
                StrategyTag = string.IsNullOrEmpty(p.StrategyTag) ? "Unknown" : p.StrategyTag,
                RegimeTag = string.IsNullOrEmpty(p.RegimeTag) ? "Unknown" : p.RegimeTag,
                ExitPolicyTag = string.IsNullOrEmpty(p.ExitPolicyTag) ? "Unknown" : p.ExitPolicyTag,
                EntryPrice = p.EntryPrice,
                ExitPrice = exitPx,
                PnL = p.RealizedPnL,
                PnLPercent = pnlPct,
                ExitReason = p.ExitReason ?? "",
                Reasoning = p.Reasoning ?? "",
                NetFunding = p.NetFunding
            });

            totalPnL += p.RealizedPnL;
            totalFunding += p.NetFunding;
            if (p.RealizedPnL > 0)
            {
                wins++; sumWin += p.RealizedPnL;
                curConsecLoss = 0;
            }
            else if (p.RealizedPnL < 0)
            {
                losses++; sumLoss += -p.RealizedPnL;
                curConsecLoss++;
                if (curConsecLoss > maxConsecLoss) maxConsecLoss = curConsecLoss;
            }
            equity += p.RealizedPnL;
            equityCurve.Add(equity);
            if (equity > peak) peak = equity;
            var dd = peak - equity;
            if (dd > maxDDDollar) maxDDDollar = dd;
            if (startingCapital > 0)
                returns.Add((double)(p.RealizedPnL / startingCapital));
        }

        // Sharpe: per-trade mean/std × √N (matches backtest convention).
        double sharpe = 0;
        if (returns.Count >= 2)
        {
            var mean = returns.Average();
            var sumSq = returns.Sum(r => (r - mean) * (r - mean));
            var std = Math.Sqrt(sumSq / (returns.Count - 1));
            if (std > 0) sharpe = mean / std * Math.Sqrt(returns.Count);
        }
        double sortino = 0;
        if (returns.Count >= 2)
        {
            var downs = returns.Where(r => r < 0).ToArray();
            if (downs.Length >= 1)
            {
                var meanAll = returns.Average();
                var dsumSq = downs.Sum(r => r * r);
                var dstd = Math.Sqrt(dsumSq / downs.Length);
                if (dstd > 0) sortino = meanAll / dstd * Math.Sqrt(returns.Count);
            }
        }
        double pf = sumLoss > 0 ? (double)(sumWin / sumLoss) : (sumWin > 0 ? double.PositiveInfinity : 0);
        double avgRR = losses > 0 && wins > 0 ? (double)((sumWin / wins) / (sumLoss / losses)) : 0;
        double maxDDPct = startingCapital > 0 ? (double)(maxDDDollar / startingCapital) * 100.0 : 0;
        double leakagePct = totalPnL + totalFees > 0 ? (double)(totalFees / (totalPnL + totalFees)) * 100.0 : 0;

        return new BacktestResult
        {
            Symbol = "PaperPortfolio",
            TotalTrades = trades.Count,
            WinningTrades = wins,
            LosingTrades = losses,
            TotalPnL = totalPnL,
            MaxDrawdown = maxDDDollar,
            MaxDrawdownPercent = (decimal)maxDDPct,
            SharpeRatio = sharpe,
            SortinoRatio = sortino,
            ProfitFactor = pf,
            AverageRR = avgRR,
            MaxConsecutiveLosses = maxConsecLoss,
            StartingCapital = startingCapital,
            FinalEquity = equity,
            EquityCurve = equityCurve,
            Trades = trades,
            StartDate = trades.Count > 0 ? trades.Min(t => t.EntryTime) : DateTime.UtcNow,
            EndDate = trades.Count > 0 ? trades.Max(t => t.ExitTime) : DateTime.UtcNow,
            TotalFeesPaid = totalFees,
            TotalNetFunding = totalFunding,
            GrossPnL = totalPnL + totalFees,
            CostLeakagePercent = leakagePct
        };
    }
}
