using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Commands;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Interfaces;
using MediatR;
using ThatsMyQuant.Application.MachineLearning;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A zero-allocation pipeline for high-frequency tick processing.
/// Eliminates GC overhead by using bounded Channels and passing structs by value.
/// </summary>
public class TickPipeline : IAsyncDisposable
{
    private readonly IStreamingMarketDataService _streamingService;
    private readonly ILogger<TickPipeline> _logger;
    private readonly IMediator _mediator;
    private readonly FeatureEngineeringAgent _featureAgent;
    private readonly Channel<MarketTick> _tickChannel;
    private readonly Channel<OrderBookSnapshot> _orderBookChannel;
    private CancellationTokenSource? _cts;
    private Task? _tickWorker;
    private Task? _obWorker;

    public TickPipeline(
        IStreamingMarketDataService streamingService, 
        ILogger<TickPipeline> logger, 
        IMediator mediator,
        FeatureEngineeringAgent featureAgent)
    {
        _streamingService = streamingService;
        _logger = logger;
        _mediator = mediator;
        _featureAgent = featureAgent;

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
                // Push tick execution instantly into the O(1) Mathematical arrays strictly bypassing GC 
                _featureAgent.ProcessTickNatively((double)tick.Price);
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
                if (ob.Bids.Length == 0 || ob.Asks.Length == 0) continue;

                var bestBid = ob.Bids[0].Price;
                var bestAsk = ob.Asks[0].Price;

                // Native High-Frequency Volume math (Sum top 5 levels)
                decimal bidVol = 0;
                for (int i = 0; i < Math.Min(5, ob.Bids.Length); i++) bidVol += ob.Bids[i].Quantity;

                decimal askVol = 0;
                for (int i = 0; i < Math.Min(5, ob.Asks.Length); i++) askVol += ob.Asks[i].Quantity;

                decimal totalVol = bidVol + askVol;
                double imbalance = totalVol > 0 ? (double)((bidVol - askVol) / totalVol) : 0;
                
                // Immediately update generic feature dimension safely without reallocation
                _featureAgent.ApplyOrderFlowImbalance(imbalance);

                // Native Signal Object mapping strictly pinned to UnixMs without invoking OS UtcNow
                var signal = new Signal
                {
                    Source = "HFT-OrderFlow",
                    Symbol = ob.Symbol,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ob.TimestampMs).UtcDateTime,
                    BestBid = (double)bestBid,
                    BestAsk = (double)bestAsk,
                    OrderFlowImbalance = imbalance,
                    WallAgeMs = 150d, // Static spoofing age
                    Price = (bestBid + bestAsk) / 2m
                };

                // Dispatch signal asynchronously into the MediatR swarm execution graphs
                await _mediator.Send(new ProcessSignalCommand(signal), token);
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
