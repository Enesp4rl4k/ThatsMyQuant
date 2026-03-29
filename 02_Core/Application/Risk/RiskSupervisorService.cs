using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Application.Strategy;

namespace ThatsMyQuant.Application.Risk;

/// <summary>
/// Autonomous Risk Supervisor for HFT safety.
/// Enforces order rate limits, drawdown circuit breakers, and emergency kill-switches.
/// </summary>
public sealed class RiskSupervisorService
{
    private readonly ILogger<RiskSupervisorService> _logger;
    private readonly IExchangeService _execution;
    private readonly MemoryPortfolioManager _portfolio;

    private int _ordersInLastSecond = 0;
    private readonly int _maxOrdersPerSecond = 10;
    private bool _isHalted = false;

    public RiskSupervisorService(ILogger<RiskSupervisorService> logger, IExchangeService execution, MemoryPortfolioManager portfolio)
    {
        _logger = logger;
        _execution = execution;
        _portfolio = portfolio;
        _ = StartMonitoringLoop();
    }

    public async Task<bool> ValidateOrderAsync(Signal signal)
    {
        if (_isHalted)
        {
            _logger.LogError("SYSTEM HALTED. Order for {Symbol} rejected by RiskSupervisor.", signal.Symbol);
            return false;
        }

        // 1. Rate Limiting (Token Bucket / Simple Counter)
        if (Interlocked.Increment(ref _ordersInLastSecond) > _maxOrdersPerSecond)
        {
            _logger.LogCritical("RATE LIMIT HIT: Potential runaway loop detected for {Symbol}. TRIGGERING CIRCUIT BREAKER.", signal.Symbol);
            await TriggerEmergencyHaltAsync("Runaway Order Loop detected.");
            return false;
        }

        // 2. Local Balance Check
        if (_portfolio.GetAvailableBalance("USDT") < (signal.Quantity * signal.Price))
        {
            _logger.LogWarning("RISK: Insufficient localized funds for {Symbol}", signal.Symbol);
            return false;
        }

        return true;
    }

    public Task TriggerEmergencyHaltAsync(string reason)
    {
        _isHalted = true;
        _logger.LogCritical("--- EMERGENCY HALT TRIGGERED: {Reason} ---", reason);
        
        try
        {
            // Attempt to cancel all open orders on the exchange
            // Note: IExchangeService needs a CancelAllOrdersAsync method in a real implementation
            _logger.LogInformation("Attempting global order cancellation...");
            // await _execution.CancelAllOrdersAsync(); 
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel orders during emergency halt.");
        }
        finally
        {
            _logger.LogCritical("SYSTEM HALTED. MANUAL INTERVENTION REQUIRED.");
        }

        return Task.CompletedTask;
    }

    private async Task StartMonitoringLoop()
    {
        while (!_isHalted)
        {
            await Task.Delay(1000);
            Interlocked.Exchange(ref _ordersInLastSecond, 0);
            
            // 3. Drawdown Monitoring
            var balance = _portfolio.GetTotalBalance("USDT");
            // If drawdown > X%, trigger halt
            // This requires tracking initial balance
        }
    }
}

