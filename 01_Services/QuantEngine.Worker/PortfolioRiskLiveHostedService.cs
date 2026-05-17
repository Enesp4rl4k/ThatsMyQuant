using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Risk;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Periodic snapshot of portfolio-level risk: fractional Kelly recommendation,
/// historical VaR and CVaR at 95% confidence on per-trade equity returns.
///
/// Runs every <c>PortfolioRisk:IntervalMinutes</c> (default 15). Logs the report
/// and alerts when VaR or CVaR exceeds configured % of basis. Read-only — does
/// NOT change sizing automatically; the operator (or a future allocator) can
/// consume the FractionalKelly recommendation.
/// </summary>
public sealed class PortfolioRiskLiveHostedService : BackgroundService
{
    private readonly ILogger<PortfolioRiskLiveHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PaperPortfolio _portfolio;
    private readonly IRealtimeAlerter? _alerter;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly double _confidence;
    private readonly decimal _kellyFraction;
    private readonly decimal _varAlertPctOfBasis;
    private readonly decimal _cvarAlertPctOfBasis;

    public PortfolioRiskLiveHostedService(
        ILogger<PortfolioRiskLiveHostedService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        PaperPortfolio portfolio,
        IRealtimeAlerter? alerter = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _portfolio = portfolio;
        _alerter = alerter;
        _enabled = !bool.TryParse(configuration["PortfolioRisk:Enabled"], out var en) || en;
        var minutes = int.TryParse(configuration["PortfolioRisk:IntervalMinutes"], out var m) && m > 0 ? m : 15;
        _interval = TimeSpan.FromMinutes(minutes);
        _confidence = double.TryParse(configuration["PortfolioRisk:Confidence"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var c) && c > 0 && c < 1 ? c : 0.95;
        _kellyFraction = decimal.TryParse(configuration["PortfolioRisk:KellyFraction"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var k) && k > 0 ? k : 0.5m;
        _varAlertPctOfBasis = decimal.TryParse(configuration["PortfolioRisk:VarAlertPercent"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v / 100m : 0.05m;
        _cvarAlertPctOfBasis = decimal.TryParse(configuration["PortfolioRisk:CvarAlertPercent"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var cv) && cv > 0 ? cv / 100m : 0.08m;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("PortfolioRiskLiveHostedService disabled (PortfolioRisk:Enabled=false).");
            return;
        }
        _logger.LogWarning(
            "PortfolioRiskLive starting. Interval={I}m Confidence={C:P0} KellyFraction={K:P0} VaR-alert>{V:P1} CVaR-alert>{CV:P1}",
            _interval.TotalMinutes, _confidence, _kellyFraction, _varAlertPctOfBasis, _cvarAlertPctOfBasis);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                try { await RunAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "PortfolioRisk tick failed."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        List<BacktestTradeResult> trades;
        List<decimal> periodicReturns;
        decimal basis;
        lock (_portfolio)
        {
            basis = _portfolio.InitialBalance > 0m ? _portfolio.InitialBalance : 1m;
            if (_portfolio.ClosedPositions.Count == 0)
            {
                _logger.LogDebug("PortfolioRisk: no closed trades yet.");
                return;
            }
            trades = _portfolio.ClosedPositions.Select(ToBacktestTrade).ToList();

            // Build per-trade fractional returns from EquityCurve diffs (relative to basis).
            periodicReturns = new List<decimal>(_portfolio.EquityCurve.Count);
            decimal prev = basis;
            foreach (var equity in _portfolio.EquityCurve)
            {
                var ret = prev > 0 ? (equity - prev) / basis : 0m;
                periodicReturns.Add(ret);
                prev = equity;
            }
        }

        using var scope = _serviceProvider.CreateScope();
        var risk = scope.ServiceProvider.GetRequiredService<PortfolioRiskAnalytics>();
        var report = risk.BuildReport(trades, periodicReturns, _confidence, _kellyFraction);

        // 2026-05-13: ME.Logging template parser does NOT accept '$' inside placeholder
        // names ({V$:F2} / {CV$:F2} → printed as literal raw text "${V$:F2}"). Renamed
        // to VarDollars / CvarDollars; the leading '$' BEFORE the brace is preserved as
        // a literal currency sigil ("...VaR=$1.23 CVaR=$2.34").
        _logger.LogWarning(
            "PORT_RISK | basis=${Basis:F2} trades={N} returns={R} | Kelly={K:P2} VaR{C:P0}={V:P2} CVaR{C:P0}={CV:P2} | basis$ VaR=${VarDollars:F2} CVaR=${CvarDollars:F2}",
            basis, report.TradeSampleSize, report.ReturnSampleSize,
            report.FractionalKelly, report.ConfidenceLevel, report.HistoricalVaR, report.ConfidenceLevel, report.HistoricalCVaR,
            report.HistoricalVaR * basis, report.HistoricalCVaR * basis);

        // 2026-05-07 (Sprint 3.2): convert from observational to enforcing.
        // Trip the shared PortfolioRiskBlockState whenever VaR or CVaR exceeds threshold;
        // executor pre-entry checks consult this and refuse to open new positions.
        var blockState = scope.ServiceProvider.GetService<PortfolioRiskBlockState>();
        bool varBreach = report.HistoricalVaR > _varAlertPctOfBasis;
        bool cvarBreach = report.HistoricalCVaR > _cvarAlertPctOfBasis;
        if (blockState is not null)
        {
            if (varBreach || cvarBreach)
            {
                var reason =
                    $"VaR{_confidence:P0}={report.HistoricalVaR:P2} (lim {_varAlertPctOfBasis:P2})  " +
                    $"CVaR{_confidence:P0}={report.HistoricalCVaR:P2} (lim {_cvarAlertPctOfBasis:P2})";
                if (!blockState.BlockNewEntries)
                {
                    _logger.LogWarning(
                        "PORT_RISK BLOCK ENGAGED — new entries refused until next snapshot clears. {Reason}",
                        reason);
                }
                blockState.Trip(reason);
            }
            else if (blockState.BlockNewEntries)
            {
                _logger.LogWarning("PORT_RISK BLOCK CLEARED — VaR/CVaR back inside thresholds. New entries permitted.");
                blockState.Clear();
            }
        }

        if (_alerter is null) return;

        if (varBreach)
        {
            await _alerter.SendAsync(
                key: "portfolio_risk_var_breach",
                severity: AlertSeverity.Warning,
                title: $"PortfolioRisk: VaR{_confidence:P0} above threshold (BLOCK ENGAGED)",
                body:
                    $"VaR{_confidence:P0} = {report.HistoricalVaR:P2} of basis (threshold {_varAlertPctOfBasis:P2}).\n" +
                    $"Sample: {report.ReturnSampleSize} trade returns. New entries blocked until next snapshot.",
                ct: ct).ConfigureAwait(false);
        }
        if (cvarBreach)
        {
            await _alerter.SendAsync(
                key: "portfolio_risk_cvar_breach",
                severity: AlertSeverity.Warning,
                title: $"PortfolioRisk: CVaR{_confidence:P0} above threshold (BLOCK ENGAGED)",
                body:
                    $"CVaR{_confidence:P0} (expected shortfall) = {report.HistoricalCVaR:P2} of basis (threshold {_cvarAlertPctOfBasis:P2}).\n" +
                    "Worst-tail mean loss is uncomfortably large. New entries blocked until next snapshot.",
                ct: ct).ConfigureAwait(false);
        }
    }

    private static BacktestTradeResult ToBacktestTrade(PaperPosition p)
    {
        var qty = p.Quantity > 0 ? p.Quantity : Math.Max(p.OpenedQuantity, 1m);
        var exitPx = p.CurrentPrice > 0 ? p.CurrentPrice : p.EntryPrice;
        return new BacktestTradeResult
        {
            EntryTime = p.EntryTime,
            ExitTime = p.ExitTime ?? DateTime.UtcNow,
            Side = p.Side,
            StrategyTag = string.IsNullOrEmpty(p.StrategyTag) ? "Unknown" : p.StrategyTag,
            EntryPrice = p.EntryPrice,
            ExitPrice = exitPx,
            PnL = p.RealizedPnL,
            PnLPercent = p.EntryPrice > 0 ? (p.RealizedPnL / (p.EntryPrice * qty)) * 100m : 0m,
            NetFunding = p.NetFunding
        };
    }
}
