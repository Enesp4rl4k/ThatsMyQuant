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

    private readonly decimal _maxDrawdownPercent;
    private volatile bool _isHalted;
    private decimal _initialBalance;
    private decimal _peakBalance;
    private decimal _currentDrawdownPercent;

    public decimal CurrentDrawdownPercent => _currentDrawdownPercent;

    public RiskSupervisorService(
        ILogger<RiskSupervisorService> logger,
        IExchangeService execution,
        IPortfolioManager portfolio,
        decimal maxDrawdownPercent = 5.0m)
    {
        _logger = logger;
        _execution = execution;
        _portfolio = portfolio;
        _maxDrawdownPercent = maxDrawdownPercent;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _initialBalance = _portfolio.GetAvailableBalance("USDT");
        _peakBalance = _initialBalance;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, stoppingToken);

                var currentBalance = _portfolio.GetAvailableBalance("USDT");
                _peakBalance = Math.Max(_peakBalance, currentBalance);

                if (_peakBalance <= 0)
                {
                    _logger.LogWarning("RiskSupervisor: peak balance is zero or negative, skipping drawdown check.");
                    continue;
                }

                _currentDrawdownPercent = (_peakBalance - currentBalance) / _peakBalance * 100m;

                if (_currentDrawdownPercent >= _maxDrawdownPercent)
                {
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
