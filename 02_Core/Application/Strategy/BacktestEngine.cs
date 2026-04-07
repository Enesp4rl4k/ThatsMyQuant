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

        ForceCloseOpenPositionIfNeeded(candlesArray, openPosition, state, trades, fullTradeHistory);
        return BuildFinalResult(symbol, historicalCandles, trades, state, startingCapital);
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

    public async Task<PboResult> ComputePboAsync(
        List<Candle> historicalCandles,
        string symbol,
        int numSegments = 16,
        decimal startingCapital = 100_000m)
    {
        if (historicalCandles == null || historicalCandles.Count < numSegments * 60)
        {
            _logger.LogWarning("Insufficient data for PBO computation on {Symbol}", symbol);
            return new PboResult { PBO = 1.0 };
        }

        var segments = PartitionIntoSegments(historicalCandles, numSegments);
        int half = numSegments / 2;
        int totalCombinations = BinomialCoefficient(numSegments, half);

        _logger.LogInformation("Computing PBO for {Symbol} with {NumSegments} segments, {TotalCombinations} combinations...",
            symbol, numSegments, totalCombinations);

        var trainSharpeList = new List<double>();
        var testSharpeList = new List<double>();
        var logitWeights = new List<double>();
        int overfittingCount = 0;

        foreach (var combination in CombinationGenerator(numSegments, half))
        {
            var trainSegments = new List<List<Candle>>();
            var testSegments = new List<List<Candle>>();

            for (int i = 0; i < numSegments; i++)
            {
                if ((combination & (1L << i)) != 0)
                    trainSegments.Add(segments[i]);
                else
                    testSegments.Add(segments[i]);
            }

            var trainCandles = ConcatenateSegments(trainSegments);
            var testCandles = ConcatenateSegments(testSegments);

            if (trainCandles.Count < 60 || testCandles.Count < 60)
                continue;

            var trainResult = await RunOnSegmentsAsync(trainCandles, symbol, startingCapital).ConfigureAwait(false);
            var testResult = await RunOnSegmentsAsync(testCandles, symbol, startingCapital).ConfigureAwait(false);

            double trainSharpe = ComputeSharpeRatio(trainResult);
            double testSharpe = ComputeSharpeRatio(testResult);

            trainSharpeList.Add(trainSharpe);
            testSharpeList.Add(testSharpe);
            logitWeights.Add(0.0);
        }

        for (int i = 0; i < trainSharpeList.Count; i++)
        {
            int rank = trainSharpeList.Count(s => s <= trainSharpeList[i]);
            double rankProb = (double)rank / (trainSharpeList.Count + 1);
            double clampedProb = Math.Max(1e-10, Math.Min(1.0 - 1e-10, rankProb));
            logitWeights[i] = Math.Log(clampedProb / (1.0 - clampedProb));
        }

        if (trainSharpeList.Count == 0)
        {
            return new PboResult { PBO = 1.0, TotalCombinations = 0 };
        }

        double medianTrainSharpe = Median(trainSharpeList);
        double medianTestSharpe = Median(testSharpeList);

        double pbo = 0.0;
        double totalWeight = 0.0;

        for (int i = 0; i < trainSharpeList.Count; i++)
        {
            double weight = Math.Exp(logitWeights[i] - logitWeights.Max());
            totalWeight += weight;

            if (trainSharpeList[i] > medianTrainSharpe && testSharpeList[i] < medianTestSharpe)
            {
                pbo += weight;
                overfittingCount++;
            }
        }

        pbo = totalWeight > 0 ? pbo / totalWeight : 0.0;

        double expectedPerformance = testSharpeList.Count > 0 ? testSharpeList.Average() : 0.0;
        double performanceDecay = trainSharpeList.Count > 0 && trainSharpeList.Average() != 0
            ? (trainSharpeList.Average() - testSharpeList.Average()) / Math.Abs(trainSharpeList.Average())
            : 0.0;

        double stochasticDominance = testSharpeList.Count > 0
            ? testSharpeList.Count(s => s > 0) / (double)testSharpeList.Count
            : 0.0;

        return new PboResult
        {
            PBO = pbo,
            StochasticDominance = stochasticDominance,
            ExpectedPerformance = expectedPerformance,
            PerformanceDecay = performanceDecay,
            TotalCombinations = trainSharpeList.Count,
            OverfittingCount = overfittingCount,
            TrainSharpeRatios = trainSharpeList,
            TestSharpeRatios = testSharpeList,
            LogitWeights = logitWeights
        };
    }

    private List<List<Candle>> PartitionIntoSegments(List<Candle> candles, int numSegments)
    {
        var segments = new List<List<Candle>>(numSegments);
        int segmentSize = candles.Count / numSegments;
        for (int i = 0; i < numSegments; i++)
        {
            int start = i * segmentSize;
            int count = (i == numSegments - 1) ? candles.Count - start : segmentSize;
            segments.Add(candles.GetRange(start, count));
        }
        return segments;
    }

    private List<Candle> ConcatenateSegments(List<List<Candle>> segments)
    {
        var result = new List<Candle>();
        foreach (var seg in segments)
            result.AddRange(seg);
        return result;
    }

    private static IEnumerable<long> CombinationGenerator(int n, int k)
    {
        long combination = (1L << k) - 1;
        long limit = 1L << n;
        while (combination < limit)
        {
            yield return combination;
            long smallest = combination & -combination;
            long ripple = combination + smallest;
            long ones = combination ^ ripple;
            ones = (ones >> 2) / smallest;
            combination = ripple | ones;
        }
    }

    private static int BinomialCoefficient(int n, int k)
    {
        if (k > n) return 0;
        if (k == 0 || k == n) return 1;
        if (k > n / 2) k = n - k;
        long result = 1;
        for (int i = 1; i <= k; i++)
        {
            result = result * (n - i + 1) / i;
        }
        return (int)Math.Min(result, int.MaxValue);
    }

    private static double ComputeSharpeRatio(BacktestResult result)
    {
        if (result.Trades == null || result.Trades.Count < 2) return 0.0;
        var returns = result.Trades.Select(t => (double)t.PnL).ToList();
        double mean = returns.Average();
        double variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        double stdDev = Math.Sqrt(Math.Max(variance, 0));
        return stdDev > 0 ? mean / stdDev * Math.Sqrt(252) : 0.0;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0.0;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }

    private async Task<BacktestResult> RunOnSegmentsAsync(List<Candle> candles, string symbol, decimal startingCapital)
    {
        var state = new BacktestState(startingCapital);
        var trades = new List<BacktestTradeResult>();
        var fullTradeHistory = new List<Trade>();
        PaperPosition? openPosition = null;

        var candlesArray = candles.ToArray();
        for (int i = 60; i < candles.Count; i++)
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

        ForceCloseOpenPositionIfNeeded(candlesArray, openPosition, state, trades, fullTradeHistory);
        return BuildFinalResult(symbol, candles, trades, state, startingCapital);
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

    private static decimal CalculatePnLPercent(decimal entryPrice, decimal exitPrice, TradeSide side)
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
        decimal qty = price > 0
            ? Math.Round((currentEquity * decision.PositionSizePercent) / price, 6, MidpointRounding.AwayFromZero)
            : 0;
        
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
            Confidence = Math.Min(99, Math.Max(10, (int)consensusConfidence)),
            StrategyTag = decision.StrategyTag,
            RegimeTag = decision.RegimeTag,
            ExitPolicyTag = decision.ExitPolicyTag
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

    private static BacktestResult BuildFinalResult(string sym, IReadOnlyList<Candle> candles, List<BacktestTradeResult> trades, BacktestState state, decimal start)
    {
        var profits = trades.Where(t => t.PnL > 0).Select(t => t.PnL).ToList();
        var losses = trades.Where(t => t.PnL <= 0).Select(t => t.PnL).ToList();
        var equityCurve = BuildEquityCurve(start, trades);

        return new BacktestResult 
        { 
            Symbol = sym, TotalTrades = trades.Count, TotalPnL = state.Equity - start, 
            FinalEquity = state.Equity, MaxDrawdownPercent = state.MaxDDPercent, Trades = trades,
            WinningTrades = trades.Count(t => t.PnL > 0),
            LosingTrades = trades.Count(t => t.PnL <= 0),
            StartingCapital = start,
            ProfitFactor = losses.Count == 0 ? (profits.Count > 0 ? 9.99 : 0.0) : (double)(profits.Sum() / Math.Abs(losses.Sum())),
            SharpeRatio = ComputeSharpeRatioFromTrades(trades),
            SortinoRatio = ComputeSortinoRatioFromTrades(trades),
            AverageRR = ComputeAverageRiskReward(trades),
            MaxConsecutiveLosses = ComputeMaxConsecutiveLosses(trades),
            StartDate = candles.Count > 0 ? candles[0].Timestamp : default,
            EndDate = candles.Count > 0 ? candles[^1].Timestamp : default,
            EquityCurve = equityCurve
        };
    }

    private static void ForceCloseOpenPositionIfNeeded(
        IReadOnlyList<Candle> candles,
        PaperPosition? openPosition,
        BacktestState state,
        List<BacktestTradeResult> trades,
        List<Trade> fullTradeHistory)
    {
        if (openPosition == null || candles.Count == 0)
        {
            return;
        }

        var lastCandle = candles[^1];
        var netPnL = CalculateNetPnl(openPosition.EntryPrice, lastCandle.Close, openPosition.Quantity, openPosition.Side);
        state.Update(netPnL);

        trades.Add(new BacktestTradeResult
        {
            EntryTime = openPosition.EntryTime,
            ExitTime = lastCandle.Timestamp,
            Side = openPosition.Side,
            PnL = netPnL,
            ExitReason = "EndOfData",
            EntryPrice = openPosition.EntryPrice,
            ExitPrice = lastCandle.Close,
            PnLPercent = CalculatePnLPercent(openPosition.EntryPrice, lastCandle.Close, openPosition.Side),
            Reasoning = openPosition.Reasoning,
            Confidence = openPosition.Confidence,
            StrategyTag = openPosition.StrategyTag,
            RegimeTag = openPosition.RegimeTag,
            ExitPolicyTag = openPosition.ExitPolicyTag
        });

        fullTradeHistory.Add(MapClosedPosition(openPosition, lastCandle.Timestamp, lastCandle.Close, netPnL));
    }

    private static Trade MapClosedPosition(PaperPosition position, DateTime exitTime, decimal exitPrice, decimal pnl) => new()
    {
        Symbol = position.Symbol,
        Side = position.Side,
        EntryPrice = position.EntryPrice,
        ExitPrice = exitPrice,
        Quantity = position.Quantity,
        EntryTime = position.EntryTime,
        ExitTime = exitTime,
        StrategyTag = position.StrategyTag,
        RegimeTag = position.RegimeTag,
        ExitPolicyTag = position.ExitPolicyTag,
        Status = TradeStatus.Executed,
        PromotionStage = "Backtest",
        ExecutionMode = "SIMULATED",
        ClosedQuantity = position.Quantity,
        AverageExitPrice = exitPrice
    };

    private static List<decimal> BuildEquityCurve(decimal startingCapital, IReadOnlyList<BacktestTradeResult> trades)
    {
        var curve = new List<decimal>(trades.Count + 1) { startingCapital };
        var equity = startingCapital;
        foreach (var trade in trades.OrderBy(t => t.ExitTime))
        {
            equity += trade.PnL;
            curve.Add(equity);
        }

        return curve;
    }

    private static double ComputeSharpeRatioFromTrades(IReadOnlyList<BacktestTradeResult> trades)
    {
        if (trades.Count < 2)
        {
            return 0.0;
        }

        var returns = trades.Select(t => (double)t.PnL).ToList();
        var mean = returns.Average();
        var variance = returns.Sum(r => Math.Pow(r - mean, 2)) / (returns.Count - 1);
        var stdDev = Math.Sqrt(Math.Max(variance, 0));
        return stdDev > 0 ? mean / stdDev * Math.Sqrt(252) : 0.0;
    }

    private static double ComputeSortinoRatioFromTrades(IReadOnlyList<BacktestTradeResult> trades)
    {
        if (trades.Count < 2)
        {
            return 0.0;
        }

        var returns = trades.Select(t => (double)t.PnL).ToList();
        var mean = returns.Average();
        var downsideReturns = returns.Where(r => r < 0).ToList();
        if (downsideReturns.Count == 0)
        {
            return mean > 0 ? 9.99 : 0.0;
        }

        var downsideVariance = downsideReturns.Sum(r => r * r) / downsideReturns.Count;
        var downsideDeviation = Math.Sqrt(Math.Max(downsideVariance, 0));
        return downsideDeviation > 0 ? mean / downsideDeviation * Math.Sqrt(252) : 0.0;
    }

    private static double ComputeAverageRiskReward(IReadOnlyList<BacktestTradeResult> trades)
    {
        var winners = trades.Where(t => t.PnL > 0).Select(t => (double)t.PnL).ToList();
        var losers = trades.Where(t => t.PnL < 0).Select(t => Math.Abs((double)t.PnL)).ToList();
        if (winners.Count == 0 || losers.Count == 0)
        {
            return 0.0;
        }

        return winners.Average() / losers.Average();
    }

    private static int ComputeMaxConsecutiveLosses(IReadOnlyList<BacktestTradeResult> trades)
    {
        var current = 0;
        var max = 0;
        foreach (var trade in trades.OrderBy(t => t.ExitTime))
        {
            if (trade.PnL < 0)
            {
                current++;
                max = Math.Max(max, current);
            }
            else
            {
                current = 0;
            }
        }

        return max;
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
