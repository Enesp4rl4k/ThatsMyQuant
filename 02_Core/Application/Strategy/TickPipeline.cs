using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Interfaces;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A zero-allocation pipeline for high-frequency tick processing.
/// Eliminates GC overhead by using bounded Channels and passing structs by value.
/// </summary>
public class TickPipeline : IAsyncDisposable
{
    private readonly IStreamingMarketDataService _streamingService;
    private readonly ILogger<TickPipeline> _logger;
    private readonly Channel<MarketTick> _tickChannel;
    private readonly Channel<OrderBookSnapshot> _orderBookChannel;
    private CancellationTokenSource? _cts;
    private Task? _tickWorker;
    private Task? _obWorker;

    public TickPipeline(IStreamingMarketDataService streamingService, ILogger<TickPipeline> logger)
    {
        _streamingService = streamingService;
        _logger = logger;

        // Bounded channel to drop oldest ticks if execution falls behind, preventing OOM
        _tickChannel = Channel.CreateBounded<MarketTick>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true
        });

        _orderBookChannel = Channel.CreateBounded<OrderBookSnapshot>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true
        });

        _streamingService.OnTickReceived += tick => _tickChannel.Writer.TryWrite(tick);
        _streamingService.OnOrderBookUpdated += ob => _orderBookChannel.Writer.TryWrite(ob);
    }

    public void Start(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _tickWorker = Task.Factory.StartNew(() => ProcessTicksAsync(_cts.Token), TaskCreationOptions.LongRunning);
        _obWorker = Task.Factory.StartNew(() => ProcessOrderBooksAsync(_cts.Token), TaskCreationOptions.LongRunning);
        _logger.LogInformation("Zero-allocation Tick Pipeline started.");
    }

    private async Task ProcessTicksAsync(CancellationToken token)
    {
        try
        {
            await foreach (var tick in _tickChannel.Reader.ReadAllAsync(token))
            {
                // TODO: Route tick to Quantitative Strategies (Math models)
                // Struct is passed by value, zero heap allocation
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in tick pipeline worker.");
        }
    }

    private async Task ProcessOrderBooksAsync(CancellationToken token)
    {
        try
        {
            await foreach (var ob in _orderBookChannel.Reader.ReadAllAsync(token))
            {
                // TODO: Route OrderBook depth to Arbitrage / Order Flow Imbalance models
                // Fast path processing without string manipulation or class instantiation
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in orderbook pipeline worker.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _tickChannel.Writer.Complete();
            _orderBookChannel.Writer.Complete();
            if (_tickWorker != null) await _tickWorker;
            if (_obWorker != null) await _obWorker;
            _cts.Dispose();
        }
    }
}
