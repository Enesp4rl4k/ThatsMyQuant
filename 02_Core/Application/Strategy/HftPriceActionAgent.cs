using Microsoft.Extensions.Logging;
using MediatR;
using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Optimized High-frequency Price Action agent using a zero-allocation circular buffer.
/// Replaces LINQ and ConcurrentQueue with high-speed sliding window math.
/// </summary>
public class HftPriceActionAgent : INotificationHandler<Signal>
{
    private readonly ILogger<HftPriceActionAgent> _logger;
    private readonly decimal[] _buffer;
    private int _head = 0;
    private int _count = 0;
    private decimal _sum = 0;
    private readonly int _windowSize;
    private readonly object _lock = new();

    public HftPriceActionAgent(ILogger<HftPriceActionAgent> logger, int windowSize = 50)
    {
        _logger = logger;
        _windowSize = windowSize;
        _buffer = new decimal[windowSize];
    }

    public Task Handle(Signal notification, CancellationToken cancellationToken)
    {
        decimal zScore = 0;
        bool detected = false;

        lock (_lock)
        {
            // Update sliding window sum
            if (_count == _windowSize)
            {
                _sum -= _buffer[_head];
            }
            else
            {
                _count++;
            }

            _buffer[_head] = notification.Price;
            _sum += notification.Price;
            _head = (_head + 1) % _windowSize;

            if (_count < _windowSize) return Task.CompletedTask;

            // Manual Mean/StdDev calculation (Single pass, zero-allocation)
            decimal mean = _sum / _windowSize;
            double sumSqDiff = 0;
            
            // Note: In ultra-HFT we would use Welford's to avoid this loop entirely, 
            // but for a 50-item window this is extremely fast and avoids LINQ allocations.
            for (int i = 0; i < _windowSize; i++)
            {
                double diff = (double)(_buffer[i] - mean);
                sumSqDiff += diff * diff;
            }

            decimal stdDev = (decimal)Math.Sqrt(sumSqDiff / _windowSize);
            zScore = stdDev == 0 ? 0 : (notification.Price - mean) / stdDev;
            
            if (Math.Abs(zScore) > 3.5m) detected = true;
        }

        if (detected)
        {
            _logger.LogInformation($"[PA DETECTED] Liquidity Sweep on {notification.Symbol}. Z-Score: {zScore:F2}.");
            notification.Metadata["PA_Signal"] = zScore > 0 ? "Exhaustion_High" : "Exhaustion_Low";
            notification.Reasoning += $" | Price Action: {notification.Metadata["PA_Signal"]}";
        }

        return Task.CompletedTask;
    }
}
