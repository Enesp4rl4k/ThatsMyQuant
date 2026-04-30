using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Analytics;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Real-time TCA — every <c>Tca:IntervalMinutes</c> (default 30), aggregates the
/// rolling-window of recently closed PaperPortfolio positions and runs the
/// post-trade <see cref="TransactionCostAnalysisService"/> to publish a
/// gross-vs-net + cost-leakage breakdown.
///
/// Alerts when <c>CostLeakagePercent &gt; AlertThresholdPercent</c> (default 30%)
/// over the rolling window — signals the 5bp/leg fee assumption is wrong, OR
/// alpha is so thin that costs dominate. Either way: actionable.
///
/// Read-only. Never closes positions or changes state. Strategy-specific buckets
/// in the report show WHICH edge is leaking.
/// </summary>
public sealed class TcaLiveAnalyzerHostedService : BackgroundService
{
    private readonly ILogger<TcaLiveAnalyzerHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PaperPortfolio _portfolio;
    private readonly IRealtimeAlerter? _alerter;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly int _windowTrades;
    private readonly double _alertThreshold;
    private readonly decimal _assumedFeeRate;

    public TcaLiveAnalyzerHostedService(
        ILogger<TcaLiveAnalyzerHostedService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        PaperPortfolio portfolio,
        IRealtimeAlerter? alerter = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _portfolio = portfolio;
        _alerter = alerter;
        _enabled = !bool.TryParse(configuration["Tca:Enabled"], out var en) || en;
        var minutes = int.TryParse(configuration["Tca:IntervalMinutes"], out var m) && m > 0 ? m : 30;
        _interval = TimeSpan.FromMinutes(minutes);
        _windowTrades = int.TryParse(configuration["Tca:WindowTrades"], out var w) && w > 0 ? w : 20;
        _alertThreshold = double.TryParse(configuration["Tca:AlertThresholdPercent"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var a) && a > 0 ? a : 30.0;
        _assumedFeeRate = decimal.TryParse(configuration["Tca:AssumedRoundTripFeeRate"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var f) && f > 0 ? f : 0.0008m;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("TcaLiveAnalyzerHostedService disabled (Tca:Enabled=false).");
            return;
        }
        _logger.LogWarning(
            "TcaLiveAnalyzer starting. Interval={I}m WindowTrades={W} AlertThreshold={T:F1}% AssumedFeeRate={F:F4}",
            _interval.TotalMinutes, _windowTrades, _alertThreshold, _assumedFeeRate);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                try { await RunAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "TCA tick failed."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        IReadOnlyList<PaperPosition> recent;
        lock (_portfolio)
        {
            var n = _portfolio.ClosedPositions.Count;
            if (n == 0) { _logger.LogDebug("TCA: no closed positions yet."); return; }
            var skip = Math.Max(0, n - _windowTrades);
            recent = _portfolio.ClosedPositions.Skip(skip).ToList();
        }

        // Project to BacktestTradeResult. Prefer the real venue-reported fee captured on
        // the position (RealFeePaid, populated by hybrid executors via GetLastFillAsync on
        // close) and only fall back to `exitPx × qty × _assumedFeeRate` when the position
        // is a pure-simulation that never touched a real venue. Mixing the two on the same
        // window would double-count fees and inflate leakage — the 2026-04-30 fee-harvest
        // showed the assumed 28bp rate over-stated reality by 2.8×.
        var trades = recent.Select(p =>
        {
            var exitPx = p.CurrentPrice > 0 ? p.CurrentPrice : p.EntryPrice;
            var qty = p.Quantity > 0 ? p.Quantity : p.OpenedQuantity;
            var feeEst = p.RealFeePaid ?? (exitPx * qty * _assumedFeeRate);
            return new BacktestTradeResult
            {
                EntryTime = p.EntryTime,
                ExitTime = p.ExitTime ?? DateTime.UtcNow,
                Side = p.Side,
                StrategyTag = string.IsNullOrEmpty(p.StrategyTag) ? "Unknown" : p.StrategyTag,
                RegimeTag = string.IsNullOrEmpty(p.RegimeTag) ? "Unknown" : p.RegimeTag,
                ExitPolicyTag = string.IsNullOrEmpty(p.ExitPolicyTag) ? "Unknown" : p.ExitPolicyTag,
                ExecutionMode = p.ExecutionMode,
                PromotionStage = p.PromotionStage,
                EntryPrice = p.EntryPrice,
                ExitPrice = exitPx,
                PnL = p.RealizedPnL,
                PnLPercent = p.EntryPrice > 0 ? (p.RealizedPnL / (p.EntryPrice * Math.Max(qty, 1m))) * 100m : 0m,
                Confidence = p.Confidence,
                ExitReason = p.ExitReason ?? "",
                Reasoning = p.Reasoning ?? "",
                TotalFeesPaid = feeEst,
                NetFunding = p.NetFunding
            };
        }).ToList();

        using var scope = _serviceProvider.CreateScope();
        var tca = scope.ServiceProvider.GetRequiredService<TransactionCostAnalysisService>();
        var report = tca.Analyse(trades);

        _logger.LogWarning(
            "TCA window: trades={N} gross=${G:F2} net=${Net:F2} fees=${F:F2} funding=${Fun:F2} leakage={L:F1}%",
            report.TradeCount, report.GrossPnL, report.NetPnL, report.TotalFees, report.NetFunding, report.CostLeakagePercent);

        // Per-strategy breakdown so we can see WHICH edge is leaking.
        foreach (var (strat, b) in report.ByStrategy)
        {
            _logger.LogInformation(
                "  TCA[{Strat}]: trades={N} gross=${G:F2} net=${Net:F2} fees=${F:F2} leakage={L:F1}%",
                strat, b.TradeCount, b.GrossPnL, b.NetPnL, b.Fees, b.CostLeakagePercent);
        }

        if (report.CostLeakagePercent > _alertThreshold && report.GrossPnL > 0m && _alerter is not null)
        {
            await _alerter.SendAsync(
                key: "tca_cost_leakage_high",
                severity: AlertSeverity.Warning,
                title: "TCA: cost leakage above threshold",
                body:
                    $"Last {report.TradeCount} closed trades:\n" +
                    $"Gross PnL: ${report.GrossPnL:F2}\nNet PnL: ${report.NetPnL:F2}\nFees: ${report.TotalFees:F2}\n" +
                    $"Cost leakage: {report.CostLeakagePercent:F1}% (threshold {_alertThreshold:F1}%).\n" +
                    $"Implication: fee assumption may be wrong, or alpha is too thin to overcome real costs.",
                ct: ct).ConfigureAwait(false);
        }
    }
}
