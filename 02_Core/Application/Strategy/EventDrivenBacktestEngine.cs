using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A true Institutional-Grade Multi-Timeframe, Multi-Strategy Event-Driven Backtest Pipeline.
/// Instead of a procedural loop checking one strategy, this engine acts as an Event Bus.
/// 
/// 1. Emits standard ticks & candles into MediatR.
/// 2. HFT/Arbitrage Agents, Pairs Trading Agents, and standard TA Agents consume events concurrently.
/// 3. The engine intercepts ExecuteTradeCommand to lock-in PnL natively.
/// </summary>
public sealed class EventDrivenBacktestEngine : IRequestHandler<ExecuteTradeCommand, Trade>
{
    private const VenueKind BacktestVenue = VenueKind.Binance;
    private readonly IMediator _mediator;
    private readonly ISharedPortfolioStateManager _portfolio;
    private readonly ILogger<EventDrivenBacktestEngine> _logger;

    public List<Trade> ExecutedTrades { get; } = new();

    public decimal StartingCapital { get; private set; }
    public decimal CurrentEquity => _portfolio.GetAvailableBalance(BacktestVenue, "USDT") + GetUnrealizedPnL();

    public EventDrivenBacktestEngine(
        IMediator mediator,
        ISharedPortfolioStateManager portfolio,
        ILogger<EventDrivenBacktestEngine> logger)
    {
        _mediator = mediator;
        _portfolio = portfolio;
        _logger = logger;
    }

    /// <summary>
    /// Feed L2 Tick data natively to all multi-frequency agents.
    /// </summary>
    public async Task RunTickReplaySessionAsync(IEnumerable<MarketTick> ticks, decimal startingCapital = 100_000m, CancellationToken ct = default)
    {
        StartingCapital = startingCapital;
        _portfolio.SetBalance(BacktestVenue, "USDT", startingCapital);

        _logger.LogInformation("Starting Multi-Agent Event-Driven TICK replay...");

        int count = 0;
        foreach (var tick in ticks)
        {
            if (ct.IsCancellationRequested) break;

            var signal = new Signal
            {
                Symbol = tick.Symbol ?? "BTC-USDT",
                Price = tick.Price,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(tick.TimestampMs).UtcDateTime,
                Source = "EventDrivenBacktestEngine:Tick",
                ExecutionAction = ExecutionActionType.None
            };

            // Non-blocking publish to all observing Agents (Arbitrage, Funding, HFT)
            await _mediator.Publish(signal, ct).ConfigureAwait(false);

            count++;
            if (count % 10_000 == 0)
                _logger.LogDebug("Replayed {Count} simultaneous ticks...", count);
        }

        _logger.LogInformation("Event-Driven Replay Complete. Total Realized Trades: {Trades}", ExecutedTrades.Count);
    }

    /// <summary>
    /// Feed 1m or 5m candles mapped to open/high/low/close signals to emulate trajectory.
    /// </summary>
    public async Task RunCandleReplaySessionAsync(IEnumerable<Candle> candles, string symbol, decimal startingCapital = 100_000m, CancellationToken ct = default)
    {
        StartingCapital = startingCapital;
        _portfolio.SetBalance(BacktestVenue, "USDT", startingCapital);

        _logger.LogInformation("Starting Multi-Agent Event-Driven CANDLE replay for {Symbol}...", symbol);

        foreach (var candle in candles)
        {
            if (ct.IsCancellationRequested) break;

            // Generate synthetic ticks out of the candle to trigger HFT logic
            var candleSignals = new[]
            {
                new Signal { Symbol = symbol, Price = candle.Open, Timestamp = candle.Timestamp, Source = "Replay:Open" },
                new Signal { Symbol = symbol, Price = candle.High, Timestamp = candle.Timestamp, Source = "Replay:High" },
                new Signal { Symbol = symbol, Price = candle.Low, Timestamp = candle.Timestamp, Source = "Replay:Low" },
                new Signal { Symbol = symbol, Price = candle.Close, Timestamp = candle.Timestamp, Source = "Replay:Close" }
            };

            foreach (var sig in candleSignals)
            {
                await _mediator.Publish(sig, ct).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Event-Driven Replay Complete. Total Realized Trades: {Trades}", ExecutedTrades.Count);
    }

    /// <summary>
    /// Intercepts Trade Orders from HFT & Swarm Agents and routes them to the simulated backtest portfolio.
    /// Lock-free interception inside the pipeline.
    /// </summary>
    public Task<Trade> Handle(ExecuteTradeCommand request, CancellationToken cancellationToken)
    {
        var sig = request.Signal;
        
        _logger.LogInformation("[Backtest Execution] {Side} {Qty} {Symbol} @ {Price} | Reason: {Reason}",
            sig.Side, sig.Quantity, sig.Symbol, sig.Price, sig.Reasoning);

        // Simulated Fill Execution 
        // 0.05% Taker Fee emulation
        decimal feeRate = 0.0005m;
        decimal notional = sig.Price * sig.Quantity;
        decimal fee = notional * feeRate;

        // Portfolio Update
        if (sig.Side == TradeSide.Buy)
        {
            _portfolio.AddBalanceDelta(BacktestVenue, "USDT", -(notional + fee));
            string asset = sig.Symbol.Replace("-USDT", "").Replace("USDT", "");
            _portfolio.AddBalanceDelta(BacktestVenue, asset, sig.Quantity);
        }
        else
        {
            string asset = sig.Symbol.Replace("-USDT", "").Replace("USDT", "");
            _portfolio.AddBalanceDelta(BacktestVenue, asset, -sig.Quantity);
            _portfolio.AddBalanceDelta(BacktestVenue, "USDT", notional - fee);
        }

        var trade = new Trade
        {
            Symbol = sig.Symbol,
            Side = sig.Side,
            Quantity = sig.Quantity,
            EntryPrice = sig.Price,
            EntryTime = sig.Timestamp,
            TotalFeesPaid = fee,
            NetFunding = 0m
        };
        ExecutedTrades.Add(trade);

        return Task.FromResult(trade);
    }

    private decimal GetUnrealizedPnL()
    {
        // For a full implementation, you'd maintain last tick price per symbol.
        // Assuming flat marking for simplistic mock equity check here.
        return 0m;
    }
}
