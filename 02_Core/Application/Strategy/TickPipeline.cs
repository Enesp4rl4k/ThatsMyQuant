using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using MediatR;
using ThatsMyQuant.Application.Common;
using ThatsMyQuant.Application.MachineLearning;

namespace ThatsMyQuant.Application.Strategy;

public sealed class TickPipeline : IAsyncDisposable
{
    private readonly IStreamingMarketDataService _streamingService;
    private readonly ILogger<TickPipeline> _logger;
    private readonly IMediator _mediator;
    private readonly FeatureEngineeringAgent _featureAgent;
    private readonly ITickLogger _tickLogger;
    private readonly Channel<MarketTick> _tickChannel;
    private readonly Channel<OrderBookSnapshot> _orderBookChannel;
    private readonly Action<MarketTick> _tickHandler;
    private readonly Action<OrderBookSnapshot> _obHandler;
    private CancellationTokenSource? _cts;
    private Task? _tickWorker;
    private Task? _obWorker;
    private bool _disposed;
    private readonly object _startLock = new();

    private readonly DataFreshnessMonitor _freshnessMonitor;

    public TickPipeline(
        IStreamingMarketDataService streamingService,
        ILogger<TickPipeline> logger,
        IMediator mediator,
        FeatureEngineeringAgent featureAgent,
        ITickLogger tickLogger,
        DataFreshnessMonitor freshnessMonitor)
    {
        _streamingService = streamingService;
        _logger = logger;
        _mediator = mediator;
        _featureAgent = featureAgent;
        _tickLogger = tickLogger;
        _freshnessMonitor = freshnessMonitor;

        // Bounded channel options optimized for single-reader/writer
        var tickOptions = new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true
        };

        var obOptions = new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true
        };

        _tickChannel = Channel.CreateBounded<MarketTick>(tickOptions);
        _orderBookChannel = Channel.CreateBounded<OrderBookSnapshot>(obOptions);

        _tickHandler = tick =>
        {
            if (tick.Price <= 0 || tick.Volume < 0) return; // price sanity guard
            _tickChannel.Writer.TryWrite(tick);
        };
        _obHandler = ob =>
        {
            if (ob.Bids.Length > 0 && ob.Bids[0].Price <= 0) return; // price sanity guard
            if (ob.Asks.Length > 0 && ob.Asks[0].Price <= 0) return;
            _orderBookChannel.Writer.TryWrite(ob);
        };
        _streamingService.OnTickReceived += _tickHandler;
        _streamingService.OnOrderBookUpdated += _obHandler;
    }

    public void Start(CancellationToken cancellationToken)
    {
        lock (_startLock)
        {
            if (_cts != null) return; // Already running — don't start a second time

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            _tickWorker = Task.Run(() => ProcessTicksAsync(_cts.Token), _cts.Token);
            _obWorker = Task.Run(() => ProcessOrderBooksAsync(_cts.Token), _cts.Token);
        }

        _logger.LogInformation("Zero-allocation Tick Pipeline started.");
    }

    private async Task ProcessTicksAsync(CancellationToken token)
    {
        try
        {
            await foreach (var tick in _tickChannel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    TradingMetrics.TicksIngested.Add(1);
                    _freshnessMonitor.RecordTick(tick.Symbol, tick.TimestampMs);

                    var priceD = (double)tick.Price;
                    if (double.IsNaN(priceD) || double.IsInfinity(priceD) || priceD <= 0)
                    {
                        TradingMetrics.TicksDropped.Add(1);
                        _logger.LogWarning("[TickPipeline] Dropping insane tick: {Symbol} price={Price}", tick.Symbol, tick.Price);
                        continue;
                    }

                    _featureAgent.ProcessTickNatively(priceD, (double)tick.Volume);
                    await _tickLogger.LogTickAsync(tick).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing tick for {Symbol}.", tick.Symbol);
                }
            }
        }
        catch (OperationCanceledException) { /* Normal shutdown */ }
    }

    private async Task ProcessOrderBooksAsync(CancellationToken token)
    {
        try
        {
            await foreach (var ob in _orderBookChannel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    if (ob.Bids.Length == 0 || ob.Asks.Length == 0) continue;

                    _freshnessMonitor.RecordOrderBook(ob.Symbol, ob.TimestampMs);

                    // Block signal dispatch if data is stale — prevents trading on old prices
                    var freshness = _freshnessMonitor.CheckFreshness(ob.Symbol);
                    if (!freshness.IsFresh)
                    {
                        _logger.LogWarning("[TickPipeline] Skipping signal for {Symbol}: {Reason}", ob.Symbol, freshness.Reason);
                        continue;
                    }

                    var (bidVol, askVol) = SumTopLevelVolumes(ob, 5);

                    decimal totalVol = bidVol + askVol;
                    double imbalance = totalVol > 0 ? (double)((bidVol - askVol) / totalVol) : 0;

                    _featureAgent.ApplyOrderFlowImbalance(imbalance);

                    var signal = new Signal
                    {
                        Source = "HFT-OrderFlow",
                        Symbol = ob.Symbol,
                        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ob.TimestampMs).UtcDateTime,
                        BestBid = (double)ob.Bids[0].Price,
                        BestAsk = (double)ob.Asks[0].Price,
                        OrderFlowImbalance = imbalance,
                        WallAgeMs = 150d,
                        Price = (ob.Bids[0].Price + ob.Asks[0].Price) * 0.5m
                    };

                    await _mediator.Send(new ProcessSignalCommand(signal), token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing order book for {Symbol}.", ob.Symbol);
                }
            }
        }
        catch (OperationCanceledException) { /* Normal shutdown */ }
    }

    private static (decimal bidVol, decimal askVol) SumTopLevelVolumes(OrderBookSnapshot ob, int levels)
    {
        decimal bVol = 0;
        decimal aVol = 0;
        int count = Math.Min(levels, Math.Min(ob.Bids.Length, ob.Asks.Length));
        
        for (int i = 0; i < count; i++)
        {
            bVol += ob.Bids[i].Quantity;
            aVol += ob.Asks[i].Quantity;
        }
        return (bVol, aVol);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _streamingService.OnTickReceived -= _tickHandler;
        _streamingService.OnOrderBookUpdated -= _obHandler;

        if (_cts != null)
        {
            await _cts.CancelAsync();
            _tickChannel.Writer.TryComplete();
            _orderBookChannel.Writer.TryComplete();

            // All workers should be awaited to ensure no background leaks
            await Task.WhenAll(_tickWorker ?? Task.CompletedTask, _obWorker ?? Task.CompletedTask).ConfigureAwait(false);
            
            // Final flush of the tick logger
            await _tickLogger.FlushAsync().ConfigureAwait(false);
            
            _cts.Dispose();
        }
        
        _logger.LogInformation("Tick Pipeline disposed cleanly.");
    }
}
