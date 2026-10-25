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
/// <remarks>
/// Drawdown is measured on account <b>equity</b> (incl. unrealized PnL), never on available
/// balance: available balance drops whenever margin is locked, so opening positions used to look
/// like a loss and could trip the halt immediately on a small account. When a trading basis is
/// configured, the USD drawdown is expressed as a percentage of that basis (so a $2K strategy
/// running inside a larger account is judged against the $2K it actually trades).
/// </remarks>
public sealed class RiskSupervisorService : BackgroundService
{
    private readonly ILogger<RiskSupervisorService> _logger;
    private readonly IExchangeService _execution;
    private readonly IPortfolioManager _portfolio;
    private readonly IOperatorAlertPublisher? _alerts;
    private readonly Func<CancellationToken, Task<decimal?>>? _equityProvider;
    private readonly decimal? _basisUsd;
    private readonly TimeSpan _equityPollInterval;

    /// <summary>Ratio of current drawdown to MaxDrawdown that triggers the HIGH-severity alert (80 %).</summary>
    private const double DrawdownAlertTierHigh = 0.8;
    /// <summary>Ratio of current drawdown to MaxDrawdown that triggers the MEDIUM-severity alert (50 %).</summary>
    private const double DrawdownAlertTierLow = 0.5;
    /// <summary>Ratio below which the alert tier resets, preventing re-alert on trivial oscillations (40 %).</summary>
    private const double DrawdownAlertResetThreshold = 0.4;

    private readonly decimal _maxDrawdownPercent;
    private readonly TimeSpan _monitoringInterval;
    private volatile bool _isHalted;
    private decimal _peakBalance;
    private decimal _currentBalance;
    private DateTime _lastEquityPollUtc = DateTime.MinValue;
    private int _zeroBalanceLogCounter;
    private bool _warnedAvailableFallback;
    private decimal _currentDrawdownPercent;
    private double _lastAlertedDrawdownTier;

    public decimal CurrentDrawdownPercent => _currentDrawdownPercent;

    public RiskSupervisorService(
        ILogger<RiskSupervisorService> logger,
        IExchangeService execution,
        IPortfolioManager portfolio,
        decimal maxDrawdownPercent = 5.0m,
        TimeSpan? monitoringInterval = null,
        IOperatorAlertPublisher? alerts = null,
        Func<CancellationToken, Task<decimal?>>? equityProvider = null,
        decimal? basisUsd = null,
        TimeSpan? equityPollInterval = null)
    {
        _logger = logger;
        _execution = execution;
        _portfolio = portfolio;
        _alerts = alerts;
        _maxDrawdownPercent = maxDrawdownPercent;
        _equityProvider = equityProvider;
        _basisUsd = basisUsd is > 0m ? basisUsd : null;
        _equityPollInterval = equityPollInterval ?? TimeSpan.FromSeconds(30);
        var interval = monitoringInterval ?? TimeSpan.FromSeconds(1);
        if (interval < TimeSpan.FromMilliseconds(100))
            interval = TimeSpan.FromMilliseconds(100);
        _monitoringInterval = interval;
    }

    /// <summary>
    /// Drawdown in percent. Denominator is the trading basis when configured (capped at the peak,
    /// so an account smaller than the basis is judged against what it really has), else the peak.
    /// </summary>
    public static decimal ComputeDrawdownPercent(decimal peakEquity, decimal currentEquity, decimal? basisUsd)
    {
        if (peakEquity <= 0m) return 0m;
        var loss = Math.Max(0m, peakEquity - currentEquity);
        var denom = basisUsd is > 0m ? Math.Min(basisUsd.Value, peakEquity) : peakEquity;
        return loss / denom * 100m;
    }

    private async Task<decimal?> ReadEquityAsync(CancellationToken ct)
    {
        if (_equityProvider is null)
        {
            if (!_warnedAvailableFallback)
            {
                _warnedAvailableFallback = true;
                _logger.LogWarning("RiskSupervisor: no equity provider configured — falling back to available balance (locked margin will read as drawdown).");
            }
            return _portfolio.GetAvailableBalance("USDT");
        }
        if (DateTime.UtcNow - _lastEquityPollUtc < _equityPollInterval && _currentBalance > 0m)
            return _currentBalance;   // throttle REST calls; reuse last reading between polls
        try
        {
            var eq = await _equityProvider(ct).ConfigureAwait(false);
            _lastEquityPollUtc = DateTime.UtcNow;
            return eq;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RiskSupervisor: equity read failed — drawdown check skipped this tick.");
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Seed the peak at startup so the first loop tick can already measure a drawdown.
        var startup = await ReadEquityAsync(stoppingToken);
        if (startup is > 0m)
        {
            _peakBalance = startup.Value;
            _currentBalance = startup.Value;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_monitoringInterval, stoppingToken);

                var equity = await ReadEquityAsync(stoppingToken);
                if (equity is null || equity <= 0m)
                {
                    // Pre-funded/paper startup before balance sync, or equity unavailable — log once
                    // per minute at Debug level instead of spamming WARN every tick.
                    if (_zeroBalanceLogCounter++ % 60 == 0)
                    {
                        _logger.LogDebug("RiskSupervisor: no equity reading yet — drawdown check skipped.");
                    }
                    continue;
                }
                _zeroBalanceLogCounter = 0;

                var currentBalance = equity.Value;
                _currentBalance = currentBalance;
                _peakBalance = Math.Max(_peakBalance, currentBalance);
                _currentDrawdownPercent = ComputeDrawdownPercent(_peakBalance, currentBalance, _basisUsd);

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
                                $"Drawdown {_currentDrawdownPercent:F2}% / Max {_maxDrawdownPercent:F2}% · Equity {currentBalance:N2} (peak {_peakBalance:N2})",
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
