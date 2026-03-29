using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

public class BacktestEngineApp
{
    private readonly ITechnicalAnalysisService _technicalAnalysis;
    private readonly IAgentCoordinator _agentCoordinator;
    private readonly ILogger<BacktestEngineApp> _logger;
    private readonly BacktestOptions _opts;

    public BacktestEngineApp(ITechnicalAnalysisService technicalAnalysis, IAgentCoordinator agentCoordinator, ILogger<BacktestEngineApp> logger, IOptions<BacktestOptions>? options = null)
    {
        _technicalAnalysis = technicalAnalysis ?? throw new ArgumentNullException(nameof(technicalAnalysis));
        _agentCoordinator = agentCoordinator ?? throw new ArgumentNullException(nameof(agentCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _opts = options?.Value ?? new BacktestOptions();
    }

    public async Task<BacktestResult> RunAsync(List<Candle> historicalCandles, string symbol, decimal startingCapital = 0m)
    {
        if (startingCapital <= 0) startingCapital = _opts.DefaultStartingCapital;
        if (historicalCandles == null || historicalCandles.Count < _opts.MinCandlesRequired)
        {
            _logger.LogWarning("Insufficient data for {Symbol}", symbol);
            return new BacktestResult { Symbol = symbol };
        }

        var state = new BacktestState(startingCapital);
        var trades = new List<BacktestTradeResult>();
        var fullTradeHistory = new List<Trade>();
        PaperPosition? openPosition = null;

        for (int i = _opts.MinCandlesRequired; i < historicalCandles.Count; i++)
        {
            var candle = historicalCandles[i];

            if (openPosition != null)
            {
                openPosition = ProcessExit(candle, openPosition, state, trades, fullTradeHistory);
            }
            else
            {
                var openTrades = new List<Trade>();
                openPosition = await TryEntry(symbol, historicalCandles, i, state, openTrades, fullTradeHistory).ConfigureAwait(false);
            }
        }

        return BuildFinalResult(symbol, trades, state, startingCapital);
    }

    private PaperPosition? ProcessExit(Candle candle, PaperPosition pos, BacktestState state, List<BacktestTradeResult> trades, List<Trade> fullHistory)
    {
        string? reason = ResolveExitReason(candle, pos);
        if (reason == null) return pos;

        // Reference exit price (SL/TP level or candle close)
        decimal rawExitPrice = candle.Close;
        if (reason == "StopLoss") rawExitPrice = pos.StopLoss;
        if (reason == "TakeProfit") rawExitPrice = pos.TakeProfit;

        // Slippage on exit always works against us:
        //   closing a long (selling) → receive less  → price * (1 - slip)
        //   closing a short (buying) → pay more      → price * (1 + slip)
        // SL exits get extra slippage (urgency premium — market orders into thin liquidity)
        decimal slipFraction = ComputeSlippage(rawExitPrice, pos.Quantity, candle.Volume);
        if (reason == "StopLoss")
            slipFraction *= 1.5m; // SL exits are urgent market orders — worse fills
        decimal exitPrice = ApplySlippage(rawExitPrice, pos.Side, slipFraction, isEntry: false);
        state.RecordSlippage(rawExitPrice * slipFraction * pos.Quantity);

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

    private async Task<PaperPosition?> TryEntry(string symbol, List<Candle> candles, int index, BacktestState state, IReadOnlyList<Trade> openTrades, IReadOnlyList<Trade> history)
    {
        var window = candles.Skip(index - _opts.MinCandlesRequired).Take(_opts.MinCandlesRequired + 1).ToList();
        var signal = new Signal { Symbol = symbol, Price = candles[index].Close };

        // 1. Technical Baseline
        var decision = await _technicalAnalysis.AnalyzeAsync(signal, window, RiskParameters.Default).ConfigureAwait(false);
        if (!decision.ShouldTrade || decision.Confidence < _opts.MinTechnicalConfidence) return null;

        // 2. Agent Intelligence Layer — detect regime from candle volatility
        var regime = DetectRegime(window);
        var snapshot = await _agentCoordinator.RunFullPipelineAsync(
            signal,
            new List<MarketTick>(),
            window,
            regime,
            openTrades,
            history).ConfigureAwait(false);

        // 3. Consensus Logic (Multi-Agent Synthesis)
        float agentAdjustment = snapshot.Votes
            .Where(v => v.Vote != VoteOutcome.Abstain)
            .Sum(v => v.ConfidenceAdjustment);

        var consensusConfidence = (double)decision.Confidence + agentAdjustment;

        // Apply Metadata Adaptation (Tier 5)
        if (snapshot.Meta != null)
            consensusConfidence *= snapshot.Meta.ConfidenceScalar;

        if (snapshot.RiskGauntlet != null && !snapshot.RiskGauntlet.Approved) return null;
        if (consensusConfidence < _opts.MinConsensusConfidence) return null;

        decimal rawEntryPrice = candles[index].Close;
        decimal candleVolume = candles[index].Volume;

        // Preliminary qty estimate for market impact calculation
        decimal estQty = rawEntryPrice > 0 ? Math.Floor((state.Equity * decision.PositionSizePercent) / rawEntryPrice) : 0;
        if (estQty <= 0) return null;

        // Market impact slippage: base + sqrt(participation_rate) penalty
        decimal slipFraction = ComputeSlippage(rawEntryPrice, estQty, candleVolume);
        decimal entryPrice = ApplySlippage(rawEntryPrice, decision.Side, slipFraction, isEntry: true);

        // Qty uses slipped price — you can afford fewer units when you pay more
        decimal desiredQty = entryPrice > 0 ? Math.Floor((state.Equity * decision.PositionSizePercent) / entryPrice) : 0;
        if (desiredQty <= 0) return null;

        // Partial fill: large orders in thin markets only partially execute
        decimal fillRatio = EstimateFillRatio(desiredQty, candleVolume);
        decimal qty = Math.Floor(desiredQty * fillRatio);
        if (qty <= 0) return null;

        state.RecordSlippage(rawEntryPrice * slipFraction * qty);
        state.RecordFill(fillRatio);

        var primaryPattern = snapshot.Patterns.PatternTags.FirstOrDefault() ?? "No Pattern";
        var voteSummary = snapshot.Votes.Any(v => v.Vote == VoteOutcome.Approve) ? " (Voted Approve)" : "";

        return new PaperPosition
        {
            Symbol = symbol, Side = decision.Side, EntryPrice = entryPrice,
            Quantity = qty, StopLoss = decision.StopLoss, TakeProfit = decision.TakeProfit,
            EntryTime = candles[index].Timestamp,
            OptimalPriceSinceEntry = entryPrice,
            Reasoning = $"Engine Consensus: {primaryPattern}{voteSummary}. {decision.Reasoning}",
            Confidence = Math.Min(99, Math.Max(10, (int)consensusConfidence))
        };
    }

    /// <summary>
    /// Depth-aware Almgren-Chriss market impact model.
    /// Base spread + sqrt(participation) impact + convex tail penalty for large orders.
    /// participationRate = orderValue / candleNotionalVolume.
    /// At >15% participation, a convex penalty kicks in to model order book depth depletion.
    /// </summary>
    private decimal ComputeSlippage(decimal price, decimal qty, decimal candleVolume)
    {
        decimal orderValue = price * qty;
        decimal candleNotional = price * candleVolume;
        if (candleNotional <= 0) return _opts.BaseSlippageFraction * _opts.ZeroVolumePenaltyMultiplier;

        decimal participationRate = orderValue / candleNotional;
        decimal sqrtImpact = _opts.ImpactCoefficient * (decimal)Math.Sqrt((double)participationRate);

        // Convex tail penalty: depth depletion beyond 15% participation
        // Models walking through multiple price levels in the order book
        decimal depthPenalty = 0m;
        if (participationRate > 0.15m)
        {
            decimal excess = participationRate - 0.15m;
            depthPenalty = _opts.ImpactCoefficient * 3m * excess * excess;
        }

        return _opts.BaseSlippageFraction + sqrtImpact + depthPenalty;
    }

    /// <summary>
    /// Estimates the fill ratio for an order given candle volume.
    /// Large orders in thin markets may only partially fill.
    /// Returns a fraction [0.1, 1.0] representing how much of the order gets filled.
    /// </summary>
    private static decimal EstimateFillRatio(decimal orderQty, decimal candleVolume, decimal maxParticipation = 0.25m)
    {
        if (candleVolume <= 0) return 0.1m; // Minimal fill in zero-volume candle
        decimal participation = orderQty / candleVolume;
        if (participation <= maxParticipation) return 1.0m; // Full fill within budget

        // Beyond max participation, fill ratio decays: you can't eat the whole book
        // At 2x max participation → ~62% fill, at 4x → ~50% fill
        decimal ratio = maxParticipation / participation;
        return Math.Max(0.1m, Math.Min(1.0m, (decimal)Math.Sqrt((double)ratio)));
    }

    /// <summary>Applies directional slippage (always works against the trader).</summary>
    private static decimal ApplySlippage(decimal price, TradeSide side, decimal slipFraction, bool isEntry)
    {
        // Entry: buying = pay more, selling = receive less
        // Exit: closing long (selling) = receive less, closing short (buying) = pay more
        bool paysMore = isEntry ? side == TradeSide.Buy : side == TradeSide.Sell;
        return paysMore ? price * (1 + slipFraction) : price * (1 - slipFraction);
    }

    /// <summary>
    /// Detects market regime from candle data using ATR ratio and directional bias.
    /// - Trending: strong directional move (price moved >2 ATR from 20-bar mean)
    /// - Volatile: high ATR relative to price but no clear direction
    /// - Ranging: low ATR, price oscillating around mean
    /// </summary>
    private MarketRegime DetectRegime(List<Candle> candles)
    {
        if (candles.Count < 20) return MarketRegime.Unknown;

        // ATR as % of price
        decimal atrSum = 0;
        for (int i = candles.Count - 14; i < candles.Count; i++)
            atrSum += candles[i].High - candles[i].Low;
        decimal atr = atrSum / 14;
        decimal price = candles[^1].Close;
        if (price <= 0) return MarketRegime.Unknown;

        decimal atrPct = atr / price;

        // Directional bias: how far price moved from 20-bar SMA
        decimal sma20 = candles.Skip(candles.Count - 20).Average(c => c.Close);
        decimal deviation = Math.Abs(price - sma20) / price;

        // High volatility + strong direction = Trending
        // High volatility + no direction = Volatile
        // Low volatility = Ranging
        if (atrPct > _opts.RegimeHighVolAtrPct)
            return deviation > _opts.RegimeHighVolTrendDeviation ? MarketRegime.Trending : MarketRegime.Volatile;

        return deviation > _opts.RegimeLowVolTrendDeviation ? MarketRegime.Trending : MarketRegime.Ranging;
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

    private decimal CalculateNetPnl(decimal entry, decimal exit, decimal qty, TradeSide side)
    {
        var direction = side == TradeSide.Sell ? -1m : 1m;
        decimal gross = (exit - entry) * qty * direction;
        decimal fees = (entry * qty + exit * qty) * _opts.TakerFeeFraction;
        return gross - fees;
    }

    private BacktestResult BuildFinalResult(string sym, List<BacktestTradeResult> trades, BacktestState state, decimal start)
    {
        var grossWin = trades.Where(t => t.PnL > 0).Sum(t => t.PnL);
        var grossLoss = Math.Abs(trades.Where(t => t.PnL <= 0).Sum(t => t.PnL));

        // Sharpe & Sortino from per-trade returns
        double sharpe = 0, sortino = 0, avgRR = 0;
        if (trades.Count >= 2)
        {
            var returns = trades.Select(t => (double)t.PnLPercent).ToArray();
            double mean = returns.Average();
            double stddev = Math.Sqrt(returns.Select(r => (r - mean) * (r - mean)).Average());
            sharpe = stddev > 0 ? mean / stddev * Math.Sqrt(252) : 0;

            var downside = returns.Where(r => r < 0).Select(r => r * r).ToArray();
            double downsideDev = downside.Length > 0 ? Math.Sqrt(downside.Average()) : 0;
            sortino = downsideDev > 0 ? mean / downsideDev * Math.Sqrt(252) : 0;
        }

        // Average risk/reward from SL/TP trades
        var slTrades = trades.Where(t => t.ExitReason == "StopLoss").ToList();
        var tpTrades = trades.Where(t => t.ExitReason == "TakeProfit").ToList();
        if (slTrades.Count > 0 && tpTrades.Count > 0)
        {
            double avgWin = (double)tpTrades.Average(t => Math.Abs(t.PnLPercent));
            double avgLoss = (double)slTrades.Average(t => Math.Abs(t.PnLPercent));
            avgRR = avgLoss > 0 ? avgWin / avgLoss : 0;
        }

        // Max consecutive losses
        int maxConsecLosses = 0, streak = 0;
        foreach (var t in trades)
        {
            if (t.PnL <= 0) { streak++; maxConsecLosses = Math.Max(maxConsecLosses, streak); }
            else streak = 0;
        }

        // Equity curve
        var equityCurve = new List<decimal> { start };
        decimal running = start;
        foreach (var t in trades)
        {
            running += t.PnL;
            equityCurve.Add(running);
        }

        // Date range
        DateTime startDate = trades.Count > 0 ? trades[0].EntryTime : DateTime.MinValue;
        DateTime endDate = trades.Count > 0 ? trades[^1].ExitTime : DateTime.MinValue;

        _logger.LogInformation(
            "[Backtest] {Symbol} — {Trades} trades | PnL: {PnL:C} | Sharpe: {Sharpe:F2} | MaxDD: {DD:F2}% | Slippage: {Slip:C}",
            sym, trades.Count, state.Equity - start, sharpe, state.MaxDDPercent, state.TotalSlippageCost);

        return new BacktestResult
        {
            Symbol = sym, TotalTrades = trades.Count, TotalPnL = state.Equity - start,
            FinalEquity = state.Equity, MaxDrawdownPercent = state.MaxDDPercent, Trades = trades,
            WinningTrades = trades.Count(t => t.PnL > 0),
            LosingTrades = trades.Count(t => t.PnL <= 0),
            StartingCapital = start,
            TotalSlippageCost = state.TotalSlippageCost,
            PartialFillCount = state.PartialFillCount,
            AverageFillRatio = state.AverageFillRatio,
            ProfitFactor = grossLoss == 0 ? (grossWin > 0 ? 9.99 : 0) : (double)(grossWin / grossLoss),
            SharpeRatio = sharpe,
            SortinoRatio = sortino,
            AverageRR = avgRR,
            MaxConsecutiveLosses = maxConsecLosses,
            EquityCurve = equityCurve,
            StartDate = startDate,
            EndDate = endDate
        };
    }

    private class BacktestState
    {
        public decimal Equity { get; private set; }
        public decimal PeakEquity { get; private set; }
        public decimal MaxDDPercent { get; private set; }
        public decimal TotalSlippageCost { get; private set; }
        public int PartialFillCount { get; private set; }
        public int TotalEntries { get; private set; }
        public double FillRatioSum { get; private set; }

        public BacktestState(decimal initial) { Equity = initial; PeakEquity = initial; }

        public void Update(decimal pnl)
        {
            Equity += pnl;
            if (Equity > PeakEquity) PeakEquity = Equity;
            decimal dd = PeakEquity > 0 ? (PeakEquity - Equity) / PeakEquity * 100 : 0;
            if (dd > MaxDDPercent) MaxDDPercent = dd;
        }

        public void RecordSlippage(decimal cost) => TotalSlippageCost += cost;

        public void RecordFill(decimal fillRatio)
        {
            TotalEntries++;
            FillRatioSum += (double)fillRatio;
            if (fillRatio < 1.0m) PartialFillCount++;
        }

        public double AverageFillRatio => TotalEntries > 0 ? FillRatioSum / TotalEntries : 1.0;
    }
}
