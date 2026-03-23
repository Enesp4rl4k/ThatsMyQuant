using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// A decorator for IExchangeService that introduces "Chaos" (latency, random failures).
/// Essential for verifying the bot's Dead-Man-Switch and error recovery before real money is at risk.
/// </summary>
public class ChaosExchangeDecorator : IExchangeService
{
    private readonly IExchangeService _inner;
    private readonly ILogger<ChaosExchangeDecorator> _logger;
    
    // Configurable failure rate (e.g. 0.05 = 5% failure probability)
    public double FailureProbability { get; set; } = 0.10; 
    public int MaxLatencyMs { get; set; } = 2000;

    public ChaosExchangeDecorator(IExchangeService inner, ILogger<ChaosExchangeDecorator> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    public async Task<Trade> ExecuteOrderAsync(Signal signal)
    {
        await SimulateChaosAsync("ExecuteOrder");

        _logger.LogWarning("[CHAOS] Passing through ExecuteOrder for {Symbol}", signal.Symbol);
        return await _inner.ExecuteOrderAsync(signal);
    }

    public async Task<Account> GetAccountStatusAsync()
    {
        await SimulateChaosAsync("GetAccountStatus");
        return await _inner.GetAccountStatusAsync();
    }

    private async Task SimulateChaosAsync(string methodName)
    {
        // 1. Simulate Network Latency (Jitter)
        if (Random.Shared.NextDouble() < 0.3) // 30% chance of high latency
        {
            int delay = Random.Shared.Next(100, MaxLatencyMs);
            _logger.LogWarning("[CHAOS] Injecting {Delay}ms latency into {Method}", delay, methodName);
            await Task.Delay(delay);
        }

        // 2. Simulate Hard API Failure
        if (Random.Shared.NextDouble() < FailureProbability)
        {
            _logger.LogError("[CHAOS] INJECTING ARTIFICIAL FAILURE in {Method}!", methodName);
            throw new TimeoutException($"[CHAOS_ERROR] Simulated exchange Timeout/Disconnect in {methodName}");
        }
    }
}
