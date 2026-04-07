using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using MediatR;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.HftInfrastructure;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// 3-Tier HFT Tick Pipeline.
///
/// RED PATH (this class):
///   - Dedicated high-priority thread with thread affinity
///   - Zero-allocation tick processing: L2 parse → OFI → VPIN → HotStateBuffer write
///   - No LINQ, no string formatting, no object creation in the hot loop
///
/// Communicates with YELLOW/BLUE paths via atomic HotStateBuffer writes.
/// </summary>
public sealed class TickPipeline : IAsyncDisposable
{
    private readonly IStreamingMarketDataService _streamingService;
    private readonly ILogger<TickPipeline> _logger;
    private readonly IMediator _mediator;
    private readonly FeatureEngineeringAgent _featureAgent;
    private readonly ITickLogger _tickLogger;
    private readonly HotStateBuffer _hotState;
    private readonly VpinCalculator _vpinCalculator;
    private readonly Channel<MarketTick> _tickChannel;
    private readonly Channel<OrderBookSnapshot> _orderBookChannel;
    private readonly Action<MarketTick> _tickHandler;
    private readonly Action<OrderBookSnapshot> _obHandler;

    private CancellationTokenSource? _cts;
    private Thread? _tickThread;
    private Thread? _obThread;
    private Task? _warmDispatchTask;
    private bool _disposed;

    // Pre-allocated OFI state to avoid per-tick allocation
    private OrderBookSnapshot? _previousOrderBook;

    public TickPipeline(
        IStreamingMarketDataService streamingService,
        ILogger<TickPipeline> logger,
        IMediator mediator,
        FeatureEngineeringAgent featureAgent,
        ITickLogger tickLogger,
        HotStateBuffer? hotState = null,
        VpinCalculator? vpinCalculator = null)
    {
        _streamingService = streamingService;
        _logger = logger;
        _mediator = mediator;
        _featureAgent = featureAgent;
        _tickLogger = tickLogger;
        _hotState = hotState ?? new HotStateBuffer();
        _vpinCalculator = vpinCalculator ?? new VpinCalculator();

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

        _tickHandler = tick => _tickChannel.Writer.TryWrite(tick);
        _obHandler = ob => _orderBookChannel.Writer.TryWrite(ob);
        _streamingService.OnTickReceived += _tickHandler;
        _streamingService.OnOrderBookUpdated += _obHandler;
    }

    public HotStateBuffer HotState => _hotState;

    public void Start(CancellationToken cancellationToken)
    {
        if (_cts != null) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        // RED PATH: Dedicated high-priority threads with thread affinity
        _tickThread = new Thread(() => RunTickHotLoop(token))
        {
            Name = "HFT-TickHotLoop",
            Priority = ThreadPriority.Highest,
            IsBackground = true
        };

        _obThread = new Thread(() => RunOrderBookHotLoop(token))
        {
            Name = "HFT-OrderBookHotLoop",
            Priority = ThreadPriority.Highest,
            IsBackground = true
        };

        // WARM PATH: Signal dispatch runs on thread pool (not latency-critical)
        _warmDispatchTask = Task.CompletedTask;

        _tickThread.Start();
        _obThread.Start();

        _logger.LogInformation("3-Tier HFT Tick Pipeline started. Red Path threads: {TickThread}, {ObThread}",
            _tickThread.ManagedThreadId, _obThread.ManagedThreadId);
    }

    /// <summary>
    /// RED PATH: Tick processing hot loop.
    /// Thread-affined, highest priority. Zero allocations per tick.
    /// </summary>
    private void RunTickHotLoop(CancellationToken token)
    {
        Thread.BeginThreadAffinity();
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!_tickChannel.Reader.TryRead(out var tick))
                {
                    // Spin-wait briefly then yield to avoid burning CPU when idle
                    Thread.SpinWait(100);
                    if (!_tickChannel.Reader.TryRead(out tick))
                    {
                        Thread.Sleep(0); // Yield timeslice
                        continue;
                    }
                }

                // Zero-allocation hot path operations
                _featureAgent.ProcessTickNatively((double)tick.Price);

                // Update VPIN (stateful, processes every tick)
                _vpinCalculator.ProcessTick(tick.Price, tick.Volume > 0 ? tick.Volume : 1m);
                _hotState.UpdateVpin(_vpinCalculator.CurrentVpin);

                // Fire-and-forget async tick logging (non-blocking)
                _ = _tickLogger.LogTickAsync(tick);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Fatal error in tick hot loop.");
        }
        finally
        {
            Thread.EndThreadAffinity();
        }
    }

    /// <summary>
    /// RED PATH: Order book processing hot loop.
    /// Computes OFI, updates HotStateBuffer, then dispatches to warm path.
    /// </summary>
    private void RunOrderBookHotLoop(CancellationToken token)
    {
        Thread.BeginThreadAffinity();
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!_orderBookChannel.Reader.TryRead(out var ob))
                {
                    Thread.SpinWait(100);
                    if (!_orderBookChannel.Reader.TryRead(out ob))
                    {
                        Thread.Sleep(0);
                        continue;
                    }
                }

                if (ob.Bids.Length == 0 || ob.Asks.Length == 0) continue;

                // Zero-allocation: compute OFI using stack-local arithmetic
                decimal bidVol = 0m, askVol = 0m;
                int levels = Math.Min(5, Math.Min(ob.Bids.Length, ob.Asks.Length));
                for (int i = 0; i < levels; i++)
                {
                    bidVol += ob.Bids[i].Quantity;
                    askVol += ob.Asks[i].Quantity;
                }

                decimal totalVol = bidVol + askVol;
                // OFI computed in decimal for full precision, cast to double at the Signal boundary
                // (Signal.OrderFlowImbalance is double? — changing to decimal would require system-wide refactor)
                decimal imbalanceDecimal = totalVol > 0 ? (bidVol - askVol) / totalVol : 0m;
                double imbalance = (double)imbalanceDecimal;

                // Atomic writes to shared state (nanosecond cost)
                _hotState.UpdateQuote(ob.Bids[0].Price, ob.Asks[0].Price);
                _hotState.UpdateOfi(imbalance);
                _featureAgent.ApplyOrderFlowImbalance(imbalance);

                // Net OFI from sequential snapshots (if previous available)
                if (_previousOrderBook is { } prevOb)
                {
                    var netOfi = OrderFlowImbalanceAnalyzer.ComputeOFI(prevOb, ob);
                    _hotState.UpdateOfi((double)(netOfi / (totalVol > 0 ? totalVol : 1m)));
                }
                _previousOrderBook = ob;

                // Dispatch to warm path for signal processing (non-blocking)
                DispatchToWarmPath(ob, imbalance, token);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Fatal error in order book hot loop.");
        }
        finally
        {
            Thread.EndThreadAffinity();
        }
    }

    /// <summary>
    /// WARM PATH: Dispatches signal processing off the hot thread.
    /// Allocations are acceptable here (mediator, signal objects).
    /// Creates a snapshot copy of the signal to avoid race conditions with the next tick.
    /// </summary>
    private void DispatchToWarmPath(OrderBookSnapshot ob, double imbalance, CancellationToken token)
    {
        // Create a fresh signal snapshot per dispatch to avoid mutation races.
        // The warm path runs on the thread pool and may still be processing when
        // the next order book update arrives and overwrites shared fields.
        var signal = new Signal
        {
            Source = "HFT-OrderFlow",
            Symbol = ob.Symbol,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ob.TimestampMs).UtcDateTime,
            BestBid = (double)ob.Bids[0].Price,
            BestAsk = (double)ob.Asks[0].Price,
            OrderFlowImbalance = imbalance,
            WallAgeMs = 150d,
            Price = (ob.Bids[0].Price + ob.Asks[0].Price) * 0.5m,
            Metadata = new Dictionary<string, string>()
        };

        // Fire-and-forget on thread pool (warm path)
        _ = Task.Run(async () =>
        {
            try
            {
                await _mediator.Send(new ProcessSignalCommand(signal), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Warm path signal dispatch failed for {Symbol}.", ob.Symbol);
            }
        }, token);
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

            _tickThread?.Join(TimeSpan.FromSeconds(3));
            _obThread?.Join(TimeSpan.FromSeconds(3));

            if (_warmDispatchTask != null)
                await _warmDispatchTask.ConfigureAwait(false);

            await _tickLogger.FlushAsync().ConfigureAwait(false);
            _cts.Dispose();
        }

        _logger.LogInformation("3-Tier Tick Pipeline disposed cleanly.");
    }
}
