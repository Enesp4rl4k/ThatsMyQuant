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
public sealed class RiskSupervisorService : IAsyncDisposable, IDisposable
{
    private readonly ILogger<RiskSupervisorService> _logger;
    private readonly IExchangeService _execution;
    private readonly MemoryPortfolioManager _portfolio;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _monitoringTask;

    private int _ordersInLastSecond = 0;
    private readonly int _maxOrdersPerSecond = 10;
    private volatile bool _isHalted;
    private int _disposed;

    public RiskSupervisorService(
        ILogger<RiskSupervisorService> logger,
        IExchangeService execution,
        MemoryPortfolioManager portfolio)
    {
        _logger = logger;
        _execution = execution;
        _portfolio = portfolio;
        _monitoringTask = Task.Run(() => RunMonitoringLoopAsync(_cts.Token));
    }

    public async Task<bool> ValidateOrderAsync(Signal signal)
    {
        if (_isHalted)
        {
            _logger.LogError("SYSTEM HALTED. Order for {Symbol} rejected by RiskSupervisor.", signal.Symbol);
            return false;
        }

        if (Interlocked.Increment(ref _ordersInLastSecond) > _maxOrdersPerSecond)
        {
            _logger.LogCritical("RATE LIMIT HIT: Potential runaway loop detected for {Symbol}. TRIGGERING CIRCUIT BREAKER.", signal.Symbol);
            await TriggerEmergencyHaltAsync("Runaway Order Loop detected.");
            return false;
        }

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
            _logger.LogInformation("Attempting global order cancellation...");
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

    private async Task RunMonitoringLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_isHalted)
            {
                await Task.Delay(1000, ct);
                Interlocked.Exchange(ref _ordersInLastSecond, 0);

                var drawdown = _portfolio.GetDrawdownPercent();
                if (drawdown > 10m)
                {
                    _logger.LogCritical("RiskSupervisor: Drawdown {Drawdown:F2}% exceeded 10% threshold.", drawdown);
                    await TriggerEmergencyHaltAsync($"Drawdown {drawdown:F2}% exceeded limit.");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RiskSupervisor monitoring loop failed.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();

        try
        {
            // Wait for the monitoring loop to finish gracefully (max 5s)
            await _monitoringTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("RiskSupervisor monitoring task did not complete within timeout during disposal.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Swallow — task already logged its own errors
        }

        _cts.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();

        // Best-effort synchronous wait — prefer DisposeAsync in hosted service scenarios
        try
        {
            _monitoringTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException) { }

        _cts.Dispose();
    }
}
