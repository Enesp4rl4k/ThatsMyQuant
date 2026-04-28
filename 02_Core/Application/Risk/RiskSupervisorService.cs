using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.Risk;

/// <summary>
/// Autonomous Risk Supervisor for HFT safety.
/// Enforces drawdown circuit breakers and emergency kill-switches via IHostedService lifecycle.
/// </summary>
public sealed class RiskSupervisorService : BackgroundService
{
    private readonly ILogger<RiskSupervisorService> _logger;
    private readonly IExchangeService _execution;
    private readonly IPortfolioManager _portfolio;
    private readonly IOperatorAlertPublisher? _alerts;

    /// <summary>Ratio of current drawdown to MaxDrawdown that triggers the HIGH-severity alert (80 %).</summary>
    private const double DrawdownAlertTierHigh = 0.8;
    /// <summary>Ratio of current drawdown to MaxDrawdown that triggers the MEDIUM-severity alert (50 %).</summary>
    private const double DrawdownAlertTierLow = 0.5;
    /// <summary>Ratio below which the alert tier resets, preventing re-alert on trivial oscillations (40 %).</summary>
    private const double DrawdownAlertResetThreshold = 0.4;

    private readonly decimal _maxDrawdownPercent;
    private readonly TimeSpan _monitoringInterval;
    private volatile bool _isHalted;
    private decimal _initialBalance;
    private decimal _peakBalance;
    private int _zeroBalanceLogCounter;
    private decimal _currentDrawdownPercent;
    private double _lastAlertedDrawdownTier;

    public decimal CurrentDrawdownPercent => _currentDrawdownPercent;

    public RiskSupervisorService(
        ILogger<RiskSupervisorService> logger,
        IExchangeService execution,
        IPortfolioManager portfolio,
        decimal maxDrawdownPercent = 5.0m,
        TimeSpan? monitoringInterval = null,
        IOperatorAlertPublisher? alerts = null)
    {
        _logger = logger;
        _execution = execution;
        _portfolio = portfolio;
        _alerts = alerts;
        _maxDrawdownPercent = maxDrawdownPercent;
        var interval = monitoringInterval ?? TimeSpan.FromSeconds(1);
        if (interval < TimeSpan.FromMilliseconds(100))
            interval = TimeSpan.FromMilliseconds(100);
        _monitoringInterval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _initialBalance = _portfolio.GetAvailableBalance("USDT");
        _peakBalance = _initialBalance;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_monitoringInterval, stoppingToken);

                var currentBalance = _portfolio.GetAvailableBalance("USDT");
                _peakBalance = Math.Max(_peakBalance, currentBalance);

                if (_peakBalance <= 0)
                {
                    // Pre-funded/paper startup before balance sync — log once per minute
                    // at Debug level instead of spamming WARN every tick.
                    if (_zeroBalanceLogCounter++ % 60 == 0)
                    {
                        _logger.LogDebug("RiskSupervisor: peak balance still zero (no funded account detected yet) — drawdown check skipped.");
                    }
                    continue;
                }
                _zeroBalanceLogCounter = 0;

                _currentDrawdownPercent = (_peakBalance - currentBalance) / _peakBalance * 100m;

                // Operator-facing tier alerts: 50% / 80% of MaxDrawdown.
                if (_alerts is not null && _maxDrawdownPercent > 0)
                {
                    var ratio = (double)(_currentDrawdownPercent / _maxDrawdownPercent);
                    var tier = ratio >= DrawdownAlertTierHigh ? DrawdownAlertTierHigh
                             : ratio >= DrawdownAlertTierLow  ? DrawdownAlertTierLow
                             : 0.0;
                    if (tier > _lastAlertedDrawdownTier)
                    {
                        _lastAlertedDrawdownTier = tier;
                        var sev   = tier >= DrawdownAlertTierHigh ? "error" : "warning";
                        var title = tier >= DrawdownAlertTierHigh ? "⚠️ Drawdown 80% of limit" : "⚠️ Drawdown 50% of limit";
                        try
                        {
                            await _alerts.PublishAsync(
                                sev,
                                "RiskSupervisor",
                                title,
                                $"Drawdown {_currentDrawdownPercent:F2}% / Max {_maxDrawdownPercent:F2}% · Balance {currentBalance:N2} (peak {_peakBalance:N2})",
                                stoppingToken);
                        }
                        catch (Exception alertEx)
                        {
                            _logger.LogWarning(alertEx, "RiskSupervisor: drawdown tier alert publish failed (tier={Tier}).", tier);
                        }
                    }
                    else if (tier < _lastAlertedDrawdownTier && ratio < DrawdownAlertResetThreshold)
                    {
                        _lastAlertedDrawdownTier = 0;
                    }
                }

                if (_currentDrawdownPercent >= _maxDrawdownPercent)
                {
                    if (_alerts is not null)
                    {
                        try
                        {
                            await _alerts.PublishAsync(
                                "error",
                                "RiskSupervisor",
                                "🛑 Drawdown limit hit — emergency halt",
                                $"Drawdown {_currentDrawdownPercent:F2}% reached MaxDrawdown {_maxDrawdownPercent:F2}%. Cancelling all orders.",
                                stoppingToken);
                        }
                        catch (Exception alertEx)
                        {
                            _logger.LogWarning(alertEx, "RiskSupervisor: emergency halt alert publish failed.");
                        }
                    }
                    await TriggerEmergencyHaltAsync(
                        $"Drawdown {_currentDrawdownPercent:F2}% >= {_maxDrawdownPercent}% threshold");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "RiskSupervisor: unhandled exception in monitoring loop, continuing.");
            }
        }
    }

    public async Task TriggerEmergencyHaltAsync(string reason)
    {
        if (_isHalted)
            return;

        _isHalted = true;
        _logger.LogCritical("--- EMERGENCY HALT TRIGGERED: {Reason} ---", reason);

        try
        {
            await _execution.CancelAllOrdersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to cancel orders during emergency halt.");
        }

        _logger.LogCritical("SYSTEM HALTED. MANUAL INTERVENTION REQUIRED.");
    }
}
