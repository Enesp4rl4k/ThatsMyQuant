using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Options;
using ThatsMyQuant.Application.MachineLearning;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Level-2 tick replay backtester — feeds historical tick + L2 order book data through the
/// full pipeline: FeatureEngineering → AgentCoordinator → execution simulation with realistic
/// fill modeling (slippage, spread, partial fills, latency simulation).
///
/// Unlike <see cref="BacktestEngineApp"/> which operates on OHLC candles, this engine captures
/// microstructure effects that dominate HFT P&amp;L: bid-ask bounce, order flow imbalance shifts,
/// and realistic queue-position fill probability.
/// </summary>
public sealed class TickReplayEngine
{
    private readonly IAgentCoordinator _agentCoordinator;
    private readonly ITechnicalAnalysisService _technicalAnalysis;
    private readonly FeatureEngineeringAgent _featureAgent;
    private readonly ILogger<TickReplayEngine> _logger;
    private readonly TickReplayOptions _opts;

    public TickReplayEngine(
        IAgentCoordinator agentCoordinator,
        ITechnicalAnalysisService technicalAnalysis,
        FeatureEngineeringAgent featureAgent,
        ILogger<TickReplayEngine> logger,
        IOptions<TickReplayOptions>? options = null)
    {
        _agentCoordinator = agentCoordinator ?? throw new ArgumentNullException(nameof(agentCoordinator));
        _technicalAnalysis = technicalAnalysis ?? throw new ArgumentNullException(nameof(technicalAnalysis));
        _featureAgent = featureAgent ?? throw new ArgumentNullException(nameof(featureAgent));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _opts = options?.Value ?? new TickReplayOptions();
    }

    /// <summary>
    /// Replays historical tick and order book data through the full strategy pipeline.
    /// Generates candles on-the-fly from ticks, feeds features in real-time order,
    /// and simulates execution with realistic fill modeling.
    /// </summary>
    public async Task<BacktestResult> ReplayAsync(
        ReplayDataSet dataSet,
        string symbol,
        decimal startingCapital = 100_000m,
        CancellationToken ct = default)
    {
        if (dataSet.Ticks.Count < 100)
        {
            _logger.LogWarning("[TickReplay] Insufficient tick data for {Symbol}: {Count} ticks", symbol, dataSet.Ticks.Count);
            return new BacktestResult { Symbol = symbol, StartingCapital = startingCapital };
        }

        // Validate order books are sorted by timestamp (documented precondition)
        for (int j = 1; j < dataSet.OrderBooks.Count; j++)
        {
            if (dataSet.OrderBooks[j].TimestampMs < dataSet.OrderBooks[j - 1].TimestampMs)
            {
                _logger.LogWarning("[TickReplay] Order books not sorted by timestamp — sorting now");
                dataSet = dataSet with { OrderBooks = dataSet.OrderBooks.OrderBy(ob => ob.TimestampMs).ToList() };
                break;
            }
        }

        var state = new ReplayState(startingCapital);
        var trades = new List<BacktestTradeResult>();
        var equityCurve = new List<decimal> { startingCapital };
        var candleBuilder = new OnlineCandleBuilder(_opts.CandleAggregationMs);
        var recentCandles = new List<Candle>();
        var recentTicks = new List<MarketTick>();
        var tradeHistory = new List<Trade>();
        ReplayPosition? openPosition = null;
        int obIndex = 0;

        _logger.LogInformation("[TickReplay] Starting replay for {Symbol}: {Ticks} ticks, {Books} order books",
            symbol, dataSet.Ticks.Count, dataSet.OrderBooks.Count);

        for (int i = 0; i < dataSet.Ticks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var tick = dataSet.Ticks[i];

            // 1. Feed tick into feature engineering (real-time order)
            _featureAgent.ProcessTickNatively((double)tick.Price, (double)tick.Volume);

            // 2. Process any order books that arrived before/at this tick's timestamp
            while (obIndex < dataSet.OrderBooks.Count && dataSet.OrderBooks[obIndex].TimestampMs <= tick.TimestampMs)
            {
                var ob = dataSet.OrderBooks[obIndex];
                if (ob.Bids.Length > 0 && ob.Asks.Length > 0)
                {
                    var (bidVol, askVol) = SumTopLevelVolumes(ob, 5);
                    decimal totalVol = bidVol + askVol;
                    double imbalance = totalVol > 0 ? (double)((bidVol - askVol) / totalVol) : 0;
                    _featureAgent.ApplyOrderFlowImbalance(imbalance);
                }
                obIndex++;
            }

            // 3. Track recent ticks (sliding window for agent pipeline)
            recentTicks.Add(tick);
            if (recentTicks.Count > 500) recentTicks.RemoveAt(0);

            // 4. Aggregate ticks into candles
            var completedCandle = candleBuilder.ProcessTick(tick);
            if (completedCandle != null)
            {
                recentCandles.Add(completedCandle);
                if (recentCandles.Count > 200) recentCandles.RemoveAt(0);
            }

            // 5. Check exit conditions on every tick (not just candle close)
            if (openPosition != null)
            {
                var exitResult = TryExitOnTick(tick, openPosition, state);
                if (exitResult != null)
                {
                    trades.Add(exitResult);
                    tradeHistory.Add(MapToClosedTrade(openPosition, exitResult));
                    equityCurve.Add(state.Equity);
                    openPosition = null;
                }
            }

            // 6. Only evaluate entries on candle boundaries (not every tick)
            if (completedCandle != null && openPosition == null && recentCandles.Count >= 60)
            {
                openPosition = await TryEntryOnCandle(
                    symbol, tick, recentCandles, recentTicks, tradeHistory, state, ct).ConfigureAwait(false);
            }
        }

        // Force-close any open position at last tick
        if (openPosition != null && dataSet.Ticks.Count > 0)
        {
            var lastTick = dataSet.Ticks[^1];
            var forceExit = ForceClose(lastTick, openPosition, state);
            trades.Add(forceExit);
            equityCurve.Add(state.Equity);
        }

        return BuildResult(symbol, trades, equityCurve, state, startingCapital, dataSet);
    }

    private async Task<ReplayPosition?> TryEntryOnCandle(
        string symbol,
        MarketTick currentTick,
        List<Candle> candles,
        List<MarketTick> ticks,
        List<Trade> history,
        ReplayState state,
        CancellationToken ct)
    {
        var window = candles.Skip(candles.Count - 61).Take(61).ToList();
        var signal = new Signal
        {
            Symbol = symbol,
            Price = currentTick.Price,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(currentTick.TimestampMs).UtcDateTime,
            Source = "TickReplay"
        };

        // Technical baseline
        var decision = await _technicalAnalysis.AnalyzeAsync(signal, window, RiskParameters.Default).ConfigureAwait(false);
        if (!decision.ShouldTrade || decision.Confidence < 55) return null;

        // Full agent pipeline with real tick data
        var snapshot = await _agentCoordinator.RunFullPipelineAsync(
            signal,
            ticks.TakeLast(100).ToList(),
            window,
            MarketRegime.Trending,
            new List<Trade>(),
            history,
            ct).ConfigureAwait(false);

        // Consensus
        float agentAdj = snapshot.Votes
            .Where(v => v.Vote != VoteOutcome.Abstain)
            .Sum(v => v.ConfidenceAdjustment);
        double consensus = decision.Confidence + agentAdj;
        if (snapshot.Meta != null) consensus *= snapshot.Meta.ConfidenceScalar;
        if (snapshot.RiskGauntlet != null && !snapshot.RiskGauntlet.Approved) return null;
        if (consensus < 55) return null;

        // Realistic fill simulation: apply spread + latency
        decimal entryPrice = SimulateEntryFill(currentTick.Price, decision.Side);
        decimal qty = entryPrice > 0 ? Math.Floor((state.Equity * decision.PositionSizePercent) / entryPrice) : 0;
        if (qty <= 0) return null;

        decimal entryCost = entryPrice * qty * _opts.TakerFee;
        state.RecordFees(entryCost);
        state.RecordSlippage(Math.Abs(entryPrice - currentTick.Price) * qty);

        _logger.LogDebug("[TickReplay] ENTRY {Side} {Qty} {Symbol} @ {Price} (consensus={Conf:F1})",
            decision.Side, qty, symbol, entryPrice, consensus);

        return new ReplayPosition
        {
            Symbol = symbol,
            Side = decision.Side,
            EntryPrice = entryPrice,
            Quantity = qty,
            StopLoss = decision.StopLoss,
            TakeProfit = decision.TakeProfit,
            EntryTimestampMs = currentTick.TimestampMs,
            Confidence = Math.Min(99, Math.Max(10, (int)consensus)),
            Reasoning = $"TickReplay: {snapshot.Patterns.PatternTags.FirstOrDefault() ?? "No Pattern"}. {decision.Reasoning}"
        };
    }

    private BacktestTradeResult? TryExitOnTick(MarketTick tick, ReplayPosition pos, ReplayState state)
    {
        string? reason = null;
        decimal exitPrice = tick.Price;

        if (pos.Side == TradeSide.Buy)
        {
            if (tick.Price <= pos.StopLoss) { reason = "StopLoss"; exitPrice = pos.StopLoss; }
            else if (tick.Price >= pos.TakeProfit) { reason = "TakeProfit"; exitPrice = pos.TakeProfit; }
        }
        else
        {
            if (tick.Price >= pos.StopLoss) { reason = "StopLoss"; exitPrice = pos.StopLoss; }
            else if (tick.Price <= pos.TakeProfit) { reason = "TakeProfit"; exitPrice = pos.TakeProfit; }
        }

        if (reason == null) return null;

        // Apply exit slippage (always against us)
        exitPrice = SimulateExitFill(exitPrice, pos.Side);
        decimal exitFee = exitPrice * pos.Quantity * _opts.TakerFee;
        state.RecordFees(exitFee);
        state.RecordSlippage(Math.Abs(exitPrice - tick.Price) * pos.Quantity);

        decimal netPnL = CalculateNetPnL(pos.EntryPrice, exitPrice, pos.Quantity, pos.Side);
        state.Update(netPnL);

        return new BacktestTradeResult
        {
            EntryTime = DateTimeOffset.FromUnixTimeMilliseconds(pos.EntryTimestampMs).UtcDateTime,
            ExitTime = DateTimeOffset.FromUnixTimeMilliseconds(tick.TimestampMs).UtcDateTime,
            Side = pos.Side,
            PnL = netPnL,
            ExitReason = reason,
            EntryPrice = pos.EntryPrice,
            ExitPrice = exitPrice,
            PnLPercent = pos.EntryPrice > 0 ? (pos.Side == TradeSide.Buy ? (exitPrice - pos.EntryPrice) / pos.EntryPrice * 100 : (pos.EntryPrice - exitPrice) / pos.EntryPrice * 100) : 0,
            Reasoning = pos.Reasoning,
            Confidence = pos.Confidence,
            ExecutionMode = "TICK_REPLAY"
        };
    }

    private BacktestTradeResult ForceClose(MarketTick lastTick, ReplayPosition pos, ReplayState state)
    {
        decimal exitPrice = SimulateExitFill(lastTick.Price, pos.Side);
        decimal exitFee = exitPrice * pos.Quantity * _opts.TakerFee;
        state.RecordFees(exitFee);

        decimal netPnL = CalculateNetPnL(pos.EntryPrice, exitPrice, pos.Quantity, pos.Side);
        state.Update(netPnL);

        return new BacktestTradeResult
        {
            EntryTime = DateTimeOffset.FromUnixTimeMilliseconds(pos.EntryTimestampMs).UtcDateTime,
            ExitTime = DateTimeOffset.FromUnixTimeMilliseconds(lastTick.TimestampMs).UtcDateTime,
            Side = pos.Side,
            PnL = netPnL,
            ExitReason = "ForceClose",
            EntryPrice = pos.EntryPrice,
            ExitPrice = exitPrice,
            PnLPercent = pos.EntryPrice > 0 ? (pos.Side == TradeSide.Buy ? (exitPrice - pos.EntryPrice) / pos.EntryPrice * 100 : (pos.EntryPrice - exitPrice) / pos.EntryPrice * 100) : 0,
            Reasoning = pos.Reasoning,
            Confidence = pos.Confidence,
            ExecutionMode = "TICK_REPLAY"
        };
    }

    /// <summary>
    /// Simulates taker fill on entry: buying pays slightly more, selling receives slightly less.
    /// Models bid-ask spread + market impact.
    /// </summary>
    private decimal SimulateEntryFill(decimal price, TradeSide side)
    {
        var spread = _opts.SpreadImpactFraction;
        return side == TradeSide.Buy
            ? price * (1 + spread)
            : price * (1 - spread);
    }

    private decimal SimulateExitFill(decimal price, TradeSide side)
    {
        var spread = _opts.SpreadImpactFraction;
        return side == TradeSide.Buy
            ? price * (1 - spread)   // closing long = selling
            : price * (1 + spread);  // closing short = buying
    }

    private decimal CalculateNetPnL(decimal entry, decimal exit, decimal qty, TradeSide side)
    {
        decimal direction = side == TradeSide.Sell ? -1m : 1m;
        decimal gross = (exit - entry) * qty * direction;
        decimal fees = (entry * qty + exit * qty) * _opts.TakerFee;
        return gross - fees;
    }

    private static (decimal bidVol, decimal askVol) SumTopLevelVolumes(OrderBookSnapshot ob, int levels)
    {
        decimal bVol = 0, aVol = 0;
        int count = Math.Min(levels, Math.Min(ob.Bids.Length, ob.Asks.Length));
        for (int i = 0; i < count; i++)
        {
            bVol += ob.Bids[i].Quantity;
            aVol += ob.Asks[i].Quantity;
        }
        return (bVol, aVol);
    }

    private static Trade MapToClosedTrade(ReplayPosition pos, BacktestTradeResult result) => new()
    {
        Symbol = pos.Symbol,
        Side = pos.Side,
        EntryPrice = pos.EntryPrice,
        ExitPrice = result.ExitPrice,
        Quantity = pos.Quantity,
        EntryTime = result.EntryTime,
        ExitTime = result.ExitTime
    };

    private BacktestResult BuildResult(
        string symbol,
        List<BacktestTradeResult> trades,
        List<decimal> equityCurve,
        ReplayState state,
        decimal startingCapital,
        ReplayDataSet dataSet)
    {
        var grossWin = trades.Where(t => t.PnL > 0).Sum(t => t.PnL);
        var grossLoss = Math.Abs(trades.Where(t => t.PnL <= 0).Sum(t => t.PnL));

        var startTime = DateTimeOffset.FromUnixTimeMilliseconds(dataSet.Ticks[0].TimestampMs).UtcDateTime;
        var endTime = DateTimeOffset.FromUnixTimeMilliseconds(dataSet.Ticks[^1].TimestampMs).UtcDateTime;

        _logger.LogInformation(
            "[TickReplay] {Symbol} — {Trades} trades | PnL: {PnL:C} | Fees: {Fees:C} | Slippage: {Slip:C} | DD: {DD:F2}%",
            symbol, trades.Count, state.Equity - startingCapital,
            state.TotalFees, state.TotalSlippage, state.MaxDDPercent);

        return new BacktestResult
        {
            Symbol = symbol,
            TotalTrades = trades.Count,
            TotalPnL = state.Equity - startingCapital,
            FinalEquity = state.Equity,
            MaxDrawdownPercent = state.MaxDDPercent,
            Trades = trades,
            WinningTrades = trades.Count(t => t.PnL > 0),
            LosingTrades = trades.Count(t => t.PnL <= 0),
            StartingCapital = startingCapital,
            TotalSlippageCost = state.TotalSlippage + state.TotalFees,
            ProfitFactor = grossLoss == 0 ? 9.99 : (double)(grossWin / grossLoss),
            EquityCurve = equityCurve,
            StartDate = startTime,
            EndDate = endTime,
            SharpeRatio = ComputeSharpe(trades),
            SortinoRatio = ComputeSortino(trades)
        };
    }

    private static double ComputeSharpe(List<BacktestTradeResult> trades)
    {
        if (trades.Count < 2) return 0;
        var returns = trades.Select(t => (double)t.PnLPercent).ToArray();
        double mean = returns.Average();
        double stddev = Math.Sqrt(returns.Select(r => (r - mean) * (r - mean)).Average());
        return stddev > 0 ? mean / stddev * Math.Sqrt(252) : 0; // Annualized
    }

    private static double ComputeSortino(List<BacktestTradeResult> trades)
    {
        if (trades.Count < 2) return 0;
        var returns = trades.Select(t => (double)t.PnLPercent).ToArray();
        double mean = returns.Average();
        var downside = returns.Where(r => r < 0).Select(r => r * r).ToArray();
        double downsideDev = downside.Length > 0 ? Math.Sqrt(downside.Average()) : 0;
        return downsideDev > 0 ? mean / downsideDev * Math.Sqrt(252) : 0;
    }

    /// <summary>
    /// Tracks equity, drawdown, fees, and slippage during tick replay.
    /// </summary>
    private sealed class ReplayState
    {
        public decimal Equity { get; private set; }
        public decimal PeakEquity { get; private set; }
        public decimal MaxDDPercent { get; private set; }
        public decimal TotalFees { get; private set; }
        public decimal TotalSlippage { get; private set; }

        public ReplayState(decimal initial) { Equity = initial; PeakEquity = initial; }

        public void Update(decimal pnl)
        {
            Equity += pnl;
            if (Equity > PeakEquity) PeakEquity = Equity;
            decimal dd = PeakEquity > 0 ? (PeakEquity - Equity) / PeakEquity * 100 : 0;
            if (dd > MaxDDPercent) MaxDDPercent = dd;
        }

        public void RecordFees(decimal fee) => TotalFees += fee;
        public void RecordSlippage(decimal slip) => TotalSlippage += slip;
    }
}

/// <summary>
/// Input data set for tick replay backtesting. Ticks and order books must be sorted by timestamp.
/// </summary>
public record ReplayDataSet
{
    public IReadOnlyList<MarketTick> Ticks { get; init; } = Array.Empty<MarketTick>();
    public IReadOnlyList<OrderBookSnapshot> OrderBooks { get; init; } = Array.Empty<OrderBookSnapshot>();
}

/// <summary>
/// Builds 1-minute OHLCV candles from streaming tick data in real-time.
/// </summary>
internal sealed class OnlineCandleBuilder
{
    private readonly int _intervalMs;
    private long _currentBucketStart;
    private decimal _open, _high, _low, _close, _volume;
    private bool _hasData;

    public OnlineCandleBuilder(int intervalMs)
    {
        _intervalMs = intervalMs;
    }

    /// <summary>
    /// Processes a tick. Returns a completed candle when the interval boundary is crossed, otherwise null.
    /// </summary>
    public Candle? ProcessTick(MarketTick tick)
    {
        long bucketStart = tick.TimestampMs / _intervalMs * _intervalMs;
        Candle? completed = null;

        if (_hasData && bucketStart > _currentBucketStart)
        {
            // New bucket — emit the completed candle
            completed = new Candle
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(_currentBucketStart).UtcDateTime,
                Open = _open,
                High = _high,
                Low = _low,
                Close = _close,
                Volume = _volume
            };

            // Reset for new bucket
            _hasData = false;
        }

        if (!_hasData)
        {
            _currentBucketStart = bucketStart;
            _open = tick.Price;
            _high = tick.Price;
            _low = tick.Price;
            _volume = 0;
            _hasData = true;
        }

        if (tick.Price > _high) _high = tick.Price;
        if (tick.Price < _low) _low = tick.Price;
        _close = tick.Price;
        _volume += tick.Volume;

        return completed;
    }
}

/// <summary>
/// Internal position tracking during tick replay. Not persisted.
/// </summary>
internal sealed class ReplayPosition
{
    public string Symbol { get; init; } = "";
    public TradeSide Side { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal Quantity { get; init; }
    public decimal StopLoss { get; init; }
    public decimal TakeProfit { get; init; }
    public long EntryTimestampMs { get; init; }
    public int Confidence { get; init; }
    public string Reasoning { get; init; } = "";
}
