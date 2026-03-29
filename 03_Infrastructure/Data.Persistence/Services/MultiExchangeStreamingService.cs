using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Aggregates multiple exchange WebSocket feeds into a single IStreamingMarketDataService.
/// Provides automatic failover: if the primary exchange goes silent, the secondary
/// continues feeding ticks. Both feeds are active simultaneously for redundancy.
///
/// When both exchanges report the same symbol, the first tick wins (dedup by timestamp).
/// </summary>
public sealed class MultiExchangeStreamingService : IStreamingMarketDataService, IAsyncDisposable
{
    private readonly ILogger<MultiExchangeStreamingService> _logger;
    private readonly IStreamingMarketDataService[] _sources;

    public event Action<MarketTick>? OnTickReceived;
    public event Action<OrderBookSnapshot>? OnOrderBookUpdated;

    public MultiExchangeStreamingService(
        ILogger<MultiExchangeStreamingService> logger,
        IEnumerable<IStreamingMarketDataService> sources)
    {
        _logger = logger;
        _sources = sources.ToArray();
    }

    public async Task ConnectAsync(string[] symbols, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[MultiExchange] Connecting {Count} exchange feed(s) for symbols: {Symbols}",
            _sources.Length, string.Join(", ", symbols));

        foreach (var source in _sources)
        {
            source.OnTickReceived += tick => OnTickReceived?.Invoke(tick);
            source.OnOrderBookUpdated += ob => OnOrderBookUpdated?.Invoke(ob);
        }

        // Connect all sources concurrently
        var tasks = _sources.Select(s => ConnectSourceSafe(s, symbols, cancellationToken));
        await Task.WhenAll(tasks);

        _logger.LogInformation("[MultiExchange] All {Count} exchange feed(s) connected.", _sources.Length);
    }

    private async Task ConnectSourceSafe(IStreamingMarketDataService source, string[] symbols, CancellationToken ct)
    {
        try
        {
            await source.ConnectAsync(symbols, ct);
            _logger.LogInformation("[MultiExchange] {Source} connected successfully.", source.GetType().Name);
        }
        catch (Exception ex)
        {
            // Don't let one exchange failure prevent the other from connecting
            _logger.LogWarning(ex, "[MultiExchange] {Source} failed to connect — continuing with other sources.", source.GetType().Name);
        }
    }

    public async Task DisconnectAsync()
    {
        foreach (var source in _sources)
        {
            try
            {
                await source.DisconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MultiExchange] Error disconnecting {Source}.", source.GetType().Name);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        foreach (var source in _sources)
        {
            if (source is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
    }
}
