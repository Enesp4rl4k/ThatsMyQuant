using MediatR;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Implements Welford's Online Algorithm for numerically stable running variance.
/// Allows for real-time Mean Reversion detection on millions of ticks without 
/// storing heavy historical arrays in RAM.
/// </summary>
public class MicroReversionAgent : INotificationHandler<Signal>
{
    private readonly ILogger<MicroReversionAgent> _logger;
    
    // Welford's State
    private long _count = 0;
    private decimal _mean = 0;
    private decimal _m2 = 0;

    public MicroReversionAgent(ILogger<MicroReversionAgent> logger)
    {
        _logger = logger;
    }

    public Task Handle(Signal notification, CancellationToken cancellationToken)
    {
        // Update Welford's Running Stats
        _count++;
        decimal delta = notification.Price - _mean;
        _mean += delta / _count;
        decimal delta2 = notification.Price - _mean;
        _m2 += delta * delta2;

        if (_count < 100) return Task.CompletedTask; // Warm-up period

        decimal variance = _m2 / (_count - 1);
        decimal stdDev = (decimal)Math.Sqrt((double)variance);

        // Mean Reversion Trigger: Price > Mean + 2.5*StdDev
        decimal upperBand = _mean + (stdDev * 2.5m);
        decimal lowerBand = _mean - (stdDev * 2.5m);

        if (notification.Price > upperBand)
        {
            _logger.LogWarning($"[REVERSION] {notification.Symbol} overextended UP. Price: {notification.Price}, Limit: {upperBand:F4}. Signal: SELL.");
            notification.Metadata["Reversion_Signal"] = "Overbought_Tick";
        }
        else if (notification.Price < lowerBand)
        {
            _logger.LogWarning($"[REVERSION] {notification.Symbol} overextended DOWN. Price: {notification.Price}, Limit: {lowerBand:F4}. Signal: BUY.");
            notification.Metadata["Reversion_Signal"] = "Oversold_Tick";
        }

        return Task.CompletedTask;
    }
}
