using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Commands;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Interfaces;
using MediatR;
using ThatsMyQuant.Application.MachineLearning;

namespace ThatsMyQuant.Application.Strategy;

public sealed class TickPipeline : IAsyncDisposable
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
    private bool _disposed;

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

        // Event handler'ları zayıf referans veya düzgün unsubscribe mantığıyla bağlamak gerekebilir
        _streamingService.OnTickReceived += tick => _tickChannel.Writer.TryWrite(tick);
        _streamingService.OnOrderBookUpdated += ob => _orderBookChannel.Writer.TryWrite(ob);
    }

    public void Start(CancellationToken cancellationToken)
    {
        if (_cts != null) return; // Zaten çalışıyorsa ikinciyi başlatma

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        // SonarQube Fix: Task.Run tercih edilir veya StartNew kullanılacaksa scheduler belirtilmelidir.
        _tickWorker = Task.Run(() => ProcessTicksAsync(_cts.Token), _cts.Token);
        _obWorker = Task.Run(() => ProcessOrderBooksAsync(_cts.Token), _cts.Token);
        
        _logger.LogInformation("Zero-allocation Tick Pipeline started.");
    }

    private async Task ProcessTicksAsync(CancellationToken token)
    {
        try
        {
            await foreach (var tick in _tickChannel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                _featureAgent.ProcessTickNatively((double)tick.Price);
            }
        }
        catch (OperationCanceledException) { /* Normal shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in tick pipeline worker.");
        }
    }

    private async Task ProcessOrderBooksAsync(CancellationToken token)
    {
        try
        {
            await foreach (var ob in _orderBookChannel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (ob.Bids.Length == 0 || ob.Asks.Length == 0) continue;

                // Pre-calculating volumes for better readability & structured math
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
        }
        catch (OperationCanceledException) { /* Normal shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in orderbook pipeline worker.");
        }
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

        if (_cts != null)
        {
            _cts.Cancel();
            _tickChannel.Writer.TryComplete();
            _orderBookChannel.Writer.TryComplete();

            // All workers should be awaited to ensure no background leaks
            await Task.WhenAll(_tickWorker ?? Task.CompletedTask, _obWorker ?? Task.CompletedTask).ConfigureAwait(false);
            
            _cts.Dispose();
        }
        
        _logger.LogInformation("Tick Pipeline disposed cleanly.");
    }
}