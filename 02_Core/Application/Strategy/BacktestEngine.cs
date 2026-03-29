using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy;

public class BacktestEngineApp
{
    private readonly ITechnicalAnalysisService _technicalAnalysis;
    private readonly IAgentCoordinator _agentCoordinator;
    private readonly TickReplayEngine? _tickReplayEngine;
    private readonly ILogger<BacktestEngineApp> _logger;

    public BacktestEngineApp(
        ITechnicalAnalysisService technicalAnalysis,
        IAgentCoordinator agentCoordinator,
        ILogger<BacktestEngineApp> logger,
        TickReplayEngine? tickReplayEngine = null)
    {
        _technicalAnalysis = technicalAnalysis ?? throw new ArgumentNullException(nameof(technicalAnalysis));
        _agentCoordinator = agentCoordinator ?? throw new ArgumentNullException(nameof(agentCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tickReplayEngine = tickReplayEngine;
    }

    public async Task<BacktestResult> RunAsync(List<Candle> historicalCandles, string symbol, decimal startingCapital = 100_000m)
    {
        if (historicalCandles == null || historicalCandles.Count < 60)
        {
            _logger.LogWarning("Insufficient data for {Symbol}", symbol);
            return new BacktestResult { Symbol = symbol };
        }

        var state = new BacktestState(startingCapital);
        var trades = new List<BacktestTradeResult>();
        var fullTradeHistory = new List<Trade>();
        PaperPosition? openPosition = null;

        // 🔥 HFT Optimization: Use a shared array and ArraySegment for zero-copy sliding windows
        var candlesArray = historicalCandles.ToArray();

        for (int i = 60; i < historicalCandles.Count; i++)
        {
            var candle = candlesArray[i];
            
            if (openPosition != null)
            {
                openPosition = ProcessExit(candle, openPosition, state, trades, fullTradeHistory);
            }
            else
            {
                var window = new ArraySegment<Candle>(candlesArray, i - 60, 61);
                openPosition = await TryEntry(symbol, window, state.Equity, fullTradeHistory).ConfigureAwait(false);
            }
        }

        return BuildFinalResult(symbol, trades, state, startingCapital);
    }

    public async Task<BacktestResult> RunTickReplayAsync(
        List<MarketTick> ticks,
        List<Candle> baseCandles,
        string symbol,
        decimal capital = 100_000m,
        List<OrderBookSnapshot>? orderBooks = null)
    {
        if (_tickReplayEngine == null)
        {
            _logger.LogWarning("TickReplayEngine not available. Register it in DI to enable L2 backtesting.");
            return new BacktestResult { Symbol = symbol, Reasoning = "TickReplayEngine not configured" };
        }

        _logger.LogInformation("Delegating to L2 TickReplayEngine for {Symbol}...", symbol);
        return await _tickReplayEngine.ReplayAsync(ticks, baseCandles, symbol, capital, orderBooks);
    }

    private PaperPosition? ProcessExit(Candle candle, PaperPosition pos, BacktestState state, List<BacktestTradeResult> trades, List<Trade> fullHistory)
    {
        string? reason = ResolveExitReason(candle, pos);
        if (reason == null) return pos;

        decimal exitPrice = candle.Close;
        if (reason == "StopLoss") exitPrice = pos.StopLoss;
        if (reason == "TakeProfit") exitPrice = pos.TakeProfit;

        decimal netPnL = CalculateNetPnl(pos.EntryPrice, exitPrice, pos.Quantity, pos.Side);
        state.Update(netPnL);

        trades.Add(new BacktestTradeResult 
        { 
            EntryTime = pos.EntryTime, 
            ExitTime = candle.Timestamp, 
            Side = pos.Side, 
            PnL = netPnL, 
            ExitReason = reason,
            EntryPrice = pos.EntryPrice,
            ExitPrice = exitPrice,
            PnLPercent = CalculatePnLPercent(pos.EntryPrice, exitPrice, pos.Side),
            Reasoning = pos.Reasoning,
            Confidence = pos.Confidence
        });

        fullHistory.Add(MapToClosedTrade(pos, candle.Timestamp, exitPrice, netPnL));
        return null;
    }

    private Trade MapToTrade(PaperPosition p) => new() { Symbol = p.Symbol, Side = p.Side, EntryPrice = p.EntryPrice, Quantity = p.Quantity, EntryTime = p.EntryTime };

    private Trade MapToClosedTrade(PaperPosition p, DateTime exitTime, decimal exitPrice, decimal pnl) => new() 
    { 
        Symbol = p.Symbol, Side = p.Side, EntryPrice = p.EntryPrice, ExitPrice = exitPrice, 
        Quantity = p.Quantity, EntryTime = p.EntryTime, ExitTime = exitTime 
    };

    private decimal CalculatePnLPercent(decimal entryPrice, decimal exitPrice, TradeSide side)
    {
        if (entryPrice <= 0) return 0;
        var rawPercent = (exitPrice - entryPrice) / entryPrice * 100;
        return side == TradeSide.Sell ? -rawPercent : rawPercent;
    }

    private async Task<PaperPosition?> TryEntry(string symbol, IReadOnlyList<Candle> window, decimal currentEquity, IReadOnlyList<Trade> history)
    {
        var candle = window[^1];
        var signal = new Signal { Symbol = symbol, Price = candle.Close };
        
        // 1. Technical Baseline
        var decision = await _technicalAnalysis.AnalyzeAsync(signal, window, RiskParameters.Default).ConfigureAwait(false);
        if (decision is null) return null;
        if (!decision.ShouldTrade || decision.Confidence < 55) return null;

        // 2. Agent Intelligence Layer (Simplified for Backtest Context)
        var snapshot = await _agentCoordinator.RunFullPipelineAsync(
            signal, 
            new List<MarketTick>(), 
            window, 
            MarketRegime.Trending, 
            new List<Trade>(), 
            history).ConfigureAwait(false)
            ?? new FiveTierSnapshot();

        // 3. Consensus Logic (Multi-Agent Synthesis)
        float agentAdjustment = (snapshot.Votes ?? Array.Empty<StrategyVote>())
            .Where(v => v.Vote != VoteOutcome.Abstain)
            .Sum(v => v.ConfidenceAdjustment);

        var consensusConfidence = (double)decision.Confidence + agentAdjustment;
        
        // Apply Metadata Adaptation (Tier 5)
        consensusConfidence *= snapshot.Meta?.ConfidenceScalar ?? 1f;

        if (snapshot.RiskGauntlet != null && !snapshot.RiskGauntlet.Approved) return null;
        if (consensusConfidence < 55) return null; 

        decimal price = candle.Close;
        decimal qty = price > 0 ? Math.Floor((currentEquity * decision.PositionSizePercent) / price) : 0;
        
        if (qty <= 0) return null;

        var primaryPattern = snapshot.Patterns?.PatternTags?.FirstOrDefault() ?? "No Pattern";
        var voteSummary = (snapshot.Votes ?? Array.Empty<StrategyVote>()).Any(v => v.Vote == VoteOutcome.Approve) ? " (Voted Approve)" : "";

        return new PaperPosition 
        { 
            Symbol = symbol, Side = decision.Side, EntryPrice = price, 
            Quantity = qty, StopLoss = decision.StopLoss, TakeProfit = decision.TakeProfit, 
            EntryTime = candle.Timestamp,
            OptimalPriceSinceEntry = price,
            Reasoning = $"Engine Consensus: {primaryPattern}{voteSummary}. {decision.Reasoning}",
            Confidence = Math.Min(99, Math.Max(10, (int)consensusConfidence))
        };
    }

    private static string? ResolveExitReason(Candle c, PaperPosition p)
    {
        if (p.Side == TradeSide.Buy)
        {
            if (c.Low <= p.StopLoss) return "StopLoss";
            if (c.High >= p.TakeProfit) return "TakeProfit";
        }
        else
        {
            if (p.Side == TradeSide.Sell)
            {
                if (c.High >= p.StopLoss) return "StopLoss";
                if (c.Low <= p.TakeProfit) return "TakeProfit";
            }
        }
        return null;
    }

    private static decimal CalculateNetPnl(decimal entry, decimal exit, decimal qty, TradeSide side)
    {
        var direction = side == TradeSide.Sell ? -1m : 1m;
        decimal gross = (exit - entry) * qty * direction;
        decimal fees = (entry * qty + exit * qty) * 0.0005m; // %0.05 fixed commission
        return gross - fees;
    }

    private static BacktestResult BuildFinalResult(string sym, List<BacktestTradeResult> trades, BacktestState state, decimal start)
    {
        return new BacktestResult 
        { 
            Symbol = sym, TotalTrades = trades.Count, TotalPnL = state.Equity - start, 
            FinalEquity = state.Equity, MaxDrawdownPercent = state.MaxDDPercent, Trades = trades,
            WinningTrades = trades.Count(t => t.PnL > 0),
            LosingTrades = trades.Count(t => t.PnL <= 0),
            StartingCapital = start,
            ProfitFactor = trades.Count(t => t.PnL <= 0) == 0 ? 9.99 : (double)(trades.Where(t => t.PnL > 0).Sum(t => t.PnL) / Math.Abs(trades.Where(t => t.PnL <= 0).Sum(t => t.PnL)))
        };
    }

    private class BacktestState 
    {
        public decimal Equity { get; private set; }
        public decimal PeakEquity { get; private set; }
        public decimal MaxDDPercent { get; private set; }

        public BacktestState(decimal initial) { Equity = initial; PeakEquity = initial; }

        public void Update(decimal pnl) 
        {
            Equity += pnl;
            if (Equity > PeakEquity) PeakEquity = Equity;
            decimal dd = PeakEquity > 0 ? (PeakEquity - Equity) / PeakEquity * 100 : 0;
            if (dd > MaxDDPercent) MaxDDPercent = dd;
        }
    }
}
