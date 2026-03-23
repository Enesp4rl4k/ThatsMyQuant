using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Application.Strategy;

public class BacktestEngineApp
{
    private readonly ITechnicalAnalysisService _technicalAnalysis;
    private readonly ILogger<BacktestEngineApp> _logger;

    public BacktestEngineApp(ITechnicalAnalysisService technicalAnalysis, ILogger<BacktestEngineApp> logger)
    {
        _technicalAnalysis = technicalAnalysis ?? throw new ArgumentNullException(nameof(technicalAnalysis));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        PaperPosition? openPosition = null;

        for (int i = 60; i < historicalCandles.Count; i++)
        {
            var candle = historicalCandles[i];
            
            if (openPosition != null)
            {
                openPosition = ProcessExit(candle, openPosition, state, trades);
            }
            else
            {
                openPosition = await TryEntry(symbol, historicalCandles, i, state.Equity).ConfigureAwait(false);
            }
        }

        return BuildFinalResult(symbol, trades, state, startingCapital);
    }

    private PaperPosition? ProcessExit(Candle candle, PaperPosition pos, BacktestState state, List<BacktestTradeResult> trades)
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
            ExitReason = reason 
        });

        return null;
    }

    private async Task<PaperPosition?> TryEntry(string symbol, List<Candle> candles, int index, decimal currentEquity)
    {
        var window = candles.Skip(index - 60).Take(61).ToList();
        var signal = new Signal { Symbol = symbol, Price = candles[index].Close };
        var decision = await _technicalAnalysis.AnalyzeAsync(signal, window, RiskParameters.Default).ConfigureAwait(false);

        if (!decision.ShouldTrade || decision.Confidence < 60) return null;

        decimal price = candles[index].Close;
        decimal qty = price > 0 ? Math.Floor((currentEquity * decision.PositionSizePercent) / price) : 0;
        
        if (qty <= 0) return null;

        return new PaperPosition 
        { 
            Symbol = symbol, Side = decision.Side.ToString(), EntryPrice = price, 
            Quantity = qty, StopLoss = decision.StopLoss, TakeProfit = decision.TakeProfit, 
            EntryTime = candles[index].Timestamp 
        };
    }

    private static string? ResolveExitReason(Candle c, PaperPosition p)
    {
        if (p.Side == "Buy")
        {
            if (c.Low <= p.StopLoss) return "StopLoss";
            if (c.High >= p.TakeProfit) return "TakeProfit";
        }
        else
        {
            if (c.High >= p.StopLoss) return "StopLoss";
            if (c.Low <= p.TakeProfit) return "TakeProfit";
        }
        return null;
    }

    private static decimal CalculateNetPnl(decimal entry, decimal exit, decimal qty, string side)
    {
        var direction = side == "Sell" ? -1m : 1m;
        decimal gross = (exit - entry) * qty * direction;
        decimal fees = (entry * qty + exit * qty) * 0.0005m; // %0.05 sabit komisyon
        return gross - fees;
    }

    private static BacktestResult BuildFinalResult(string sym, List<BacktestTradeResult> trades, BacktestState state, decimal start)
    {
        return new BacktestResult 
        { 
            Symbol = sym, TotalTrades = trades.Count, TotalPnL = state.Equity - start, 
            FinalEquity = state.Equity, MaxDrawdownPercent = state.MaxDDPercent, Trades = trades 
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