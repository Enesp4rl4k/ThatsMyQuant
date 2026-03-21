using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.HftInfrastructure;

/// <summary>
/// Aggregates live websocket feeds from multiple Tier-1 exchanges.
/// Normalizes disparate JSON formats into a single high-performance HftRingBuffer stream.
/// </summary>
public class CrossExchangeFeedManager : BackgroundService
{
    private readonly ILogger<CrossExchangeFeedManager> _logger;
    private readonly HftRingBuffer<Domain.Entities.Signal> _ringBuffer;
    private readonly AllocationFreeSignalPool _signalPool;

    public CrossExchangeFeedManager(
        ILogger<CrossExchangeFeedManager> logger,
        HftRingBuffer<Domain.Entities.Signal> ringBuffer,
        AllocationFreeSignalPool signalPool)
    {
        _logger = logger;
        _ringBuffer = ringBuffer;
        _signalPool = signalPool;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Cross-Exchange Multi-Feed Aggregator...");

        // In a live environment, these would be separate ClientWebSocket loops
        // Here we simulate the parallel ingestion from Binance and a mock ByBit feed
        var binanceTask = StartMockExchangeFeed("Binance", 0.0001m, stoppingToken);
        var bybitTask = StartMockExchangeFeed("ByBit", 0.00015m, stoppingToken);

        await Task.WhenAll(binanceTask, bybitTask);
    }

    private async Task StartMockExchangeFeed(string exchangeName, decimal drift, CancellationToken token)
    {
        _logger.LogInformation($"Connecting to {exchangeName} liquidity stream...");
        
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Simulate a price around $65,000 for BTC/USDT with slight drift between exchanges
                decimal simulatedPrice = 65000m + (decimal)(new Random().NextDouble() * 100) * (1 + drift);
                
                var signal = _signalPool.Rent($"BTCUSDT-{exchangeName}", simulatedPrice);
                bool success = _ringBuffer.TryEnqueue(signal);
                
                if (!success) _signalPool.Return(signal);

                // Exchanges emit trades every 50-200ms
                await Task.Delay(150, token);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Feed Error on {exchangeName}: {ex.Message}");
                await Task.Delay(2000, token);
            }
        }
    }
}
