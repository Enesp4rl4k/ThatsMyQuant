using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Options;
using ThatsMyQuant.Application.MachineLearning;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Level-2 Tick Replay Engine for high-fidelity backtesting.
/// Feeds historical tick data through the ML feature pipeline and strategy agents,
/// simulating realistic execution with slippage, partial fills, and fee modeling.
/// </summary>
public sealed class TickReplayEngine
{
    private readonly IAgentCoordinator _coordinator;
    private readonly FeatureEngineeringAgent _featureAgent;
    private readonly ILogger<TickReplayEngine> _logger;

    // Simulation parameters (configurable via MachineLearningOptions)
    private readonly decimal _commissionRate;
    private readonly double _slippageBps;
    private readonly int _minTicksForEntry;

    public TickReplayEngine(
        IAgentCoordinator coordinator,
        FeatureEngineeringAgent featureAgent,
        ILogger<TickReplayEngine> logger,
        IOptions<MachineLearningOptions>? mlOptions = null)
    {
        _coordinator = coordinator;
        _featureAgent = featureAgent;
        _logger = logger;
        var opts = mlOptions?.Value ?? new MachineLearningOptions();
        _commissionRate = opts.SimulationCommissionRate;
        _slippageBps = opts.SimulationSlippageBps;
        _minTicksForEntry = opts.MinTicksForEntry;
    }

    /// <summary>
    /// Replays historical tick data from raw CSV datasets (Parquet-convertible) directly into the agent pipeline.
    /// </summary>
    public async Task<BacktestResult> ReplayFromCsvAsync(
        string ticksCsvPath,
        string orderBooksCsvPath,
        IReadOnlyList<Candle> baseCandles,
        string symbol,
        decimal startingCapital = 100_000m,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Parsing ultra-high-fidelity L2 Data from disk for {Symbol}...", symbol);

        var ticks = new List<MarketTick>();
        await foreach(var tick in L2ExternalDataParser.ParseTicksCsvAsync(ticksCsvPath).WithCancellation(ct))
        {
            ticks.Add(tick);
        }

        var orderBooks = new List<OrderBookSnapshot>();
        await foreach(var ob in L2ExternalDataParser.ParseOrderBooksCsvAsync(orderBooksCsvPath).WithCancellation(ct))
        {
            orderBooks.Add(ob);
        }

        return await ReplayAsync(ticks, baseCandles, symbol, startingCapital, orderBooks, ct);
    }

    /// <summary>
    /// Replays historical tick data through the full agent pipeline.
    /// Returns a comprehensive BacktestResult with institutional-grade metrics.
    /// </summary>
    public async Task<BacktestResult> ReplayAsync(
        IReadOnlyList<MarketTick> ticks,
        IReadOnlyList<Candle> baseCandles,
        string symbol,
        decimal startingCapital = 100_000m,
        IReadOnlyList<OrderBookSnapshot>? orderBooks = null,
        CancellationToken ct = default)
    {
        if (ticks.Count < _minTicksForEntry)
        {
            _logger.LogWarning("Insufficient tick data for {Symbol}: {Count} < {Min}",
                symbol, ticks.Count, _minTicksForEntry);
            return new BacktestResult
            {
                Symbol = symbol,
                StartingCapital = startingCapital,
                FinalEquity = startingCapital,
                Reasoning = $"Insufficient data: {ticks.Count} ticks"
            };
        }

        _logger.LogInformation("Starting L2 Tick Replay for {Symbol}: {TickCount} ticks, {CandleCount} candles",
            symbol, ticks.Count, baseCandles.Count);

        var state = new ReplayState(startingCapital);
        var trades = new List<BacktestTradeResult>();
        var equityCurve = new List<decimal> { startingCapital };
        var tradeHistory = new List<Trade>();
        ReplayPosition? position = null;
        var orderedBooks = orderBooks?
            .Where(book => string.Equals(book.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            .OrderBy(book => book.TimestampMs)
            .ToList();
        var bookIndex = 0;
        OrderBookSnapshot? latestBook = null;
        decimal currentOFI = 0m;

        // Phase 1: Warm up the feature agent
        int warmupEnd = Math.Min(_minTicksForEntry, ticks.Count);
        for (int i = 0; i < warmupEnd; i++)
        {
            _featureAgent.ProcessTickNatively((double)ticks[i].Price);
        }

        // Phase 2: Replay with full agent pipeline
        for (int i = warmupEnd; i < ticks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var tick = ticks[i];
            _featureAgent.ProcessTickNatively((double)tick.Price);
            
            var previousBook = latestBook;
            latestBook = AdvanceOrderBook(latestBook, orderedBooks, tick.TimestampMs, ref bookIndex);
            
            if (previousBook.HasValue && latestBook.HasValue && previousBook.Value.TimestampMs != latestBook.Value.TimestampMs)
            {
                currentOFI = OrderFlowImbalanceAnalyzer.ComputeOFI(previousBook.Value, latestBook.Value);
            }

            if (position != null)
            {
                position = EvaluateOpenPosition(position, tick, state, equityCurve, trades, tradeHistory, latestBook, symbol);
            }
            else if (i % 50 == 0) // Evaluate entry every 50 ticks to avoid over-trading
            {
                position = await TryTickEntry(tick, symbol, baseCandles, state.Equity, tradeHistory, latestBook, currentOFI, ct);
            }
        }

        return BuildResult(symbol, trades, state, startingCapital, equityCurve, ticks);
    }

    private ReplayPosition? EvaluateOpenPosition(
        ReplayPosition position,
        MarketTick tick,
        ReplayState state,
        List<decimal> equityCurve,
        List<BacktestTradeResult> trades,
        List<Trade> tradeHistory,
        OrderBookSnapshot? latestBook,
        string symbol)
    {
        position.UpdatePeak(tick.Price);
        var exitReason = CheckExit(tick, position);
        if (exitReason == null) return position;

        var exitSide = position.Side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
        var execution = SimulateExecution(exitSide, tick.Price, position.Quantity, latestBook);
        if (execution.FilledQuantity <= 0) return position;

        var exitPrice = execution.FillPrice;
        var pnl = CalculatePnl(position, exitPrice, execution.FilledQuantity);
        state.Update(pnl);
        equityCurve.Add(state.Equity);

        trades.Add(new BacktestTradeResult
        {
            EntryTime = position.EntryTime,
            ExitTime = DateTimeOffset.FromUnixTimeMilliseconds(tick.TimestampMs).UtcDateTime,
            Side = position.Side,
            EntryPrice = position.EntryPrice,
            ExitPrice = exitPrice,
            PnL = pnl,
            PnLPercent = position.EntryPrice > 0
                ? (exitPrice - position.EntryPrice) / position.EntryPrice * 100m
                  * (position.Side == TradeSide.Sell ? -1 : 1)
                : 0,
            ExitReason = exitReason,
            ExecutionMode = "TICK-REPLAY",
            Confidence = position.Confidence,
            Reasoning = $"{position.Reasoning} | Exit fill {execution.FillPrice:F4} on {execution.ExecutionScope}"
        });

        tradeHistory.Add(new Trade
        {
            Symbol = symbol,
            Side = position.Side,
            EntryPrice = position.EntryPrice,
            ExitPrice = exitPrice,
            Quantity = execution.FilledQuantity,
            EntryTime = position.EntryTime,
            ExitTime = DateTimeOffset.FromUnixTimeMilliseconds(tick.TimestampMs).UtcDateTime
        });

        return null;
    }

    private async Task<ReplayPosition?> TryTickEntry(
        MarketTick tick,
        string symbol,
        IReadOnlyList<Candle> candles,
        decimal equity,
        IReadOnlyList<Trade> history,
        OrderBookSnapshot? orderBook,
        decimal currentOFI,
        CancellationToken ct)
    {
        try
        {
            var signal = new Signal
            {
                Symbol = symbol,
                Price = tick.Price,
                Source = "TickReplay"
            };

            var snapshot = await _coordinator.RunFullPipelineAsync(
                signal,
                new List<MarketTick> { tick },
                candles,
                MarketRegime.Trending,
                new List<Trade>(),
                history,
                ct);

            // Check consensus
            float adjustment = snapshot.Votes
                .Where(v => v.Vote != VoteOutcome.Abstain)
                .Sum(v => v.ConfidenceAdjustment);

            double confidence = 50 + adjustment;
            if (snapshot.Meta != null)
                confidence *= snapshot.Meta.ConfidenceScalar;

            if (snapshot.RiskGauntlet != null && !snapshot.RiskGauntlet.Approved)
                return null;

            if (confidence < 55)
                return null;

            // Determine side from votes
            int buyVotes = snapshot.Votes.Count(v => v.Vote == VoteOutcome.Approve);
            int totalVotes = snapshot.Votes.Count(v => v.Vote != VoteOutcome.Abstain);
            var side = buyVotes > totalVotes / 2 ? TradeSide.Buy : TradeSide.Sell;

            // Apply Order Flow Imbalance microstructure boost
            if (currentOFI > 0 && side == TradeSide.Buy) confidence += 5;
            if (currentOFI < 0 && side == TradeSide.Sell) confidence += 5;
            if (currentOFI < 0 && side == TradeSide.Buy) confidence -= 10; // Adverse selection (bids dropping while we buy)
            if (currentOFI > 0 && side == TradeSide.Sell) confidence -= 10;

            if (confidence < 55)
                return null; // Microstructure or pipeline vetoed entry

            decimal requestedNotional = equity * 0.02m;
            decimal provisionalPrice = orderBook.HasValue
                ? ResolveReferencePrice(tick.Price, side, orderBook.Value)
                : tick.Price; // Avoid simulating slippage before we know quantity
            decimal requestedQty = provisionalPrice > 0 ? Math.Floor(requestedNotional / provisionalPrice) : 0;
            var execution = SimulateExecution(side, tick.Price, requestedQty, orderBook);
            decimal entryPrice = execution.FillPrice;
            decimal qty = execution.FilledQuantity;
            if (qty <= 0) return null;

            // Risk levels: 1% SL, 2% TP for tick-level
            decimal atr = entryPrice * 0.01m;

            return new ReplayPosition
            {
                Side = side,
                EntryPrice = entryPrice,
                Quantity = qty,
                StopLoss = side == TradeSide.Buy ? entryPrice - atr : entryPrice + atr,
                TakeProfit = side == TradeSide.Buy ? entryPrice + atr * 2 : entryPrice - atr * 2,
                EntryTime = DateTimeOffset.FromUnixTimeMilliseconds(tick.TimestampMs).UtcDateTime,
                PeakPrice = entryPrice,
                Confidence = Math.Min(99, Math.Max(10, (int)confidence)),
                Reasoning = $"L2 Replay entry @ {confidence:F1}% confidence via {execution.ExecutionScope}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Agent pipeline failed during tick replay entry — skipping tick");
            return null;
        }
    }

    private static string? CheckExit(MarketTick tick, ReplayPosition pos)
    {
        if (pos.Side == TradeSide.Buy)
        {
            if (tick.Price <= pos.StopLoss) return "StopLoss";
            if (tick.Price >= pos.TakeProfit) return "TakeProfit";
        }
        else
        {
            if (tick.Price >= pos.StopLoss) return "StopLoss";
            if (tick.Price <= pos.TakeProfit) return "TakeProfit";
        }
        return null;
    }

    private static decimal SimulateSlippage(decimal price, TradeSide side, decimal quantity)
    {
        // Employs our institutional execution model to enforce nonlinear Square-Root slippage cost based on volume depth
        return ExecutionImpactModel.CalculateSquareRootImpactPrice(price, side, quantity, 100_000m, 0.02m);
    }

    private static ExecutionFill SimulateExecution(TradeSide side, decimal referencePrice, decimal requestedQuantity, OrderBookSnapshot? orderBook)
    {
        if (requestedQuantity <= 0)
        {
            return new ExecutionFill(0m, 0m, "NoFill");
        }

        if (orderBook is not { } book)
        {
            return new ExecutionFill(SimulateSlippage(referencePrice, side, requestedQuantity), requestedQuantity, "SyntheticSlippage");
        }

        var levels = side == TradeSide.Buy ? book.Asks : book.Bids;
        if (levels == null || levels.Length == 0)
        {
            return new ExecutionFill(SimulateSlippage(referencePrice, side, requestedQuantity), requestedQuantity, "SyntheticSlippage");
        }

        decimal remaining = requestedQuantity;
        decimal totalCost = 0m;
        foreach (var level in levels)
        {
            if (remaining <= 0) break;
            if (level.Quantity <= 0 || level.Price <= 0) continue;

            var consumed = Math.Min(level.Quantity, remaining);
            totalCost += level.Price * consumed;
            remaining -= consumed;
        }

        var filledQuantity = requestedQuantity - remaining;
        if (filledQuantity <= 0)
        {
            return new ExecutionFill(0m, 0m, "BookEmpty");
        }

        return new ExecutionFill(totalCost / filledQuantity, filledQuantity, remaining <= 0 ? "OrderBookFullFill" : "OrderBookPartialFill");
    }

    private static decimal ResolveReferencePrice(decimal fallbackPrice, TradeSide side, OrderBookSnapshot book)
    {
        var levels = side == TradeSide.Buy ? book.Asks : book.Bids;
        var firstLevelPrice = levels.FirstOrDefault().Price;
        return firstLevelPrice > 0 ? firstLevelPrice : fallbackPrice;
    }

    private static OrderBookSnapshot? AdvanceOrderBook(
        OrderBookSnapshot? current,
        IReadOnlyList<OrderBookSnapshot>? orderedBooks,
        long tickTimestampMs,
        ref int bookIndex)
    {
        if (orderedBooks == null || orderedBooks.Count == 0)
        {
            return current;
        }

        while (bookIndex < orderedBooks.Count && orderedBooks[bookIndex].TimestampMs <= tickTimestampMs)
        {
            current = orderedBooks[bookIndex];
            bookIndex++;
        }

        return current;
    }

    private decimal CalculatePnl(ReplayPosition pos, decimal exitPrice, decimal filledQuantity)
    {
        decimal direction = pos.Side == TradeSide.Sell ? -1m : 1m;
        decimal gross = (exitPrice - pos.EntryPrice) * filledQuantity * direction;
        decimal fees = (pos.EntryPrice * filledQuantity + exitPrice * filledQuantity) * _commissionRate;
        return gross - fees;
    }

    private static BacktestResult BuildResult(
        string symbol,
        List<BacktestTradeResult> trades,
        ReplayState state,
        decimal startingCapital,
        List<decimal> equityCurve,
        IReadOnlyList<MarketTick> ticks)
    {
        var winners = trades.Where(t => t.PnL > 0).ToList();
        var losers = trades.Where(t => t.PnL <= 0).ToList();

        double grossProfit = (double)winners.Sum(t => t.PnL);
        double grossLoss = Math.Abs((double)losers.Sum(t => t.PnL));

        // Sharpe Ratio (annualized, assuming ~252 trading days)
        double sharpe = 0;
        double sortino = 0;
        if (trades.Count > 1)
        {
            var returns = trades.Select(t => (double)t.PnLPercent).ToArray();
            double mean = returns.Average();
            double stdDev = Math.Sqrt(returns.Sum(r => (r - mean) * (r - mean)) / (returns.Length - 1));
            sharpe = stdDev > 0 ? mean / stdDev * Math.Sqrt(252) : 0;

            var downsideReturns = returns.Where(r => r < 0).ToArray();
            double downsideDev = downsideReturns.Length > 0
                ? Math.Sqrt(downsideReturns.Sum(r => r * r) / downsideReturns.Length)
                : 0;
            sortino = downsideDev > 0 ? mean / downsideDev * Math.Sqrt(252) : 0;
        }

        // Max consecutive losses
        int maxConsecLosses = 0, currentStreak = 0;
        foreach (var t in trades)
        {
            if (t.PnL <= 0) { currentStreak++; maxConsecLosses = Math.Max(maxConsecLosses, currentStreak); }
            else currentStreak = 0;
        }

        return new BacktestResult
        {
            Symbol = symbol,
            TotalTrades = trades.Count,
            WinningTrades = winners.Count,
            LosingTrades = losers.Count,
            TotalPnL = state.Equity - startingCapital,
            MaxDrawdownPercent = state.MaxDrawdownPercent,
            SharpeRatio = double.IsNaN(sharpe) ? 0 : sharpe,
            SortinoRatio = double.IsNaN(sortino) ? 0 : sortino,
            ProfitFactor = grossLoss > 0 ? grossProfit / grossLoss : grossProfit > 0 ? 9.99 : 0,
            MaxConsecutiveLosses = maxConsecLosses,
            StartingCapital = startingCapital,
            FinalEquity = state.Equity,
            EquityCurve = equityCurve,
            Trades = trades,
            StartDate = ticks.Count > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ticks[0].TimestampMs).UtcDateTime : DateTime.MinValue,
            EndDate = ticks.Count > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ticks[^1].TimestampMs).UtcDateTime : DateTime.MinValue,
            Reasoning = $"L2 Tick Replay: {ticks.Count} ticks, {trades.Count} trades"
        };
    }

    private sealed class ReplayState
    {
        public decimal Equity { get; private set; }
        public decimal PeakEquity { get; private set; }
        public decimal MaxDrawdownPercent { get; private set; }

        public ReplayState(decimal initial)
        {
            Equity = initial;
            PeakEquity = initial;
        }

        public void Update(decimal pnl)
        {
            Equity += pnl;
            if (Equity > PeakEquity) PeakEquity = Equity;
            decimal dd = PeakEquity > 0 ? (PeakEquity - Equity) / PeakEquity * 100 : 0;
            if (dd > MaxDrawdownPercent) MaxDrawdownPercent = dd;
        }
    }

    private sealed class ReplayPosition
    {
        public TradeSide Side { get; init; }
        public decimal EntryPrice { get; init; }
        public decimal Quantity { get; init; }
        public decimal StopLoss { get; init; }
        public decimal TakeProfit { get; init; }
        public DateTime EntryTime { get; init; }
        public decimal PeakPrice { get; set; }
        public int Confidence { get; init; }
        public string Reasoning { get; init; } = "";

        public void UpdatePeak(decimal price)
        {
            if (Side == TradeSide.Buy && price > PeakPrice) PeakPrice = price;
            if (Side == TradeSide.Sell && price < PeakPrice) PeakPrice = price;
        }
    }

    private readonly record struct ExecutionFill(decimal FillPrice, decimal FilledQuantity, string ExecutionScope);
}
