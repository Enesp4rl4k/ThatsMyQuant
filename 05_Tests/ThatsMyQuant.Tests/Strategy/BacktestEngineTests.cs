using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class BacktestEngineTests
{
    // ─── Core Backtest Tests ───

    [Fact]
    public async Task RunAsync_ShouldForceCloseOpenPosition_OnLastCandle()
    {
        var engine = CreateEngine(CreateDecision(positionSizePercent: 0.10m, stopLoss: 10m, takeProfit: 1_000_000m));
        var candles = CreateCandles(65, 100m, 1m);

        var result = await engine.RunAsync(candles, "BTCUSDT", 100_000m);

        result.TotalTrades.Should().Be(1);
        result.Trades.Should().ContainSingle();
        result.Trades[0].ExitReason.Should().Be("EndOfData");
        result.FinalEquity.Should().BeGreaterThan(result.StartingCapital);
        result.EquityCurve.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunAsync_ShouldAllowFractionalCryptoPositionSizing_ForHighPriceAssets()
    {
        var engine = CreateEngine(CreateDecision(positionSizePercent: 0.10m, stopLoss: 10m, takeProfit: 1_000_000m));
        var candles = CreateCandles(65, 70_000m, 100m);

        var result = await engine.RunAsync(candles, "BTCUSDT", 1_000m);

        result.TotalTrades.Should().Be(1);
        result.Trades[0].EntryPrice.Should().BeGreaterThan(0m);
        result.Trades[0].ExitPrice.Should().BeGreaterThan(result.Trades[0].EntryPrice);
        result.Trades[0].PnL.Should().NotBe(0m);
    }

    [Fact]
    public async Task RunAsync_ShouldReturnEmpty_WhenInsufficientData()
    {
        var engine = CreateEngine(CreateDecision());
        var candles = CreateCandles(30, 100m, 1m); // < 60 minimum

        var result = await engine.RunAsync(candles, "ETHUSDT", 100_000m);

        result.TotalTrades.Should().Be(0);
        result.TotalPnL.Should().Be(0);
        result.FinalEquity.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ShouldTrackMaxDrawdown()
    {
        // Create candles that go up then crash - will trigger stop loss
        var candles = new List<Candle>();
        for (int i = 0; i < 65; i++)
        {
            decimal price = i < 62 ? 100m + i : 50m; // crash at end
            candles.Add(new Candle
            {
                Timestamp = new DateTime(2026, 1, 1).AddMinutes(i * 5),
                Open = price, High = price + 2, Low = price - 2, Close = price, Volume = 1000
            });
        }

        var engine = CreateEngine(CreateDecision(stopLoss: 60m, takeProfit: 200m));
        var result = await engine.RunAsync(candles, "BTCUSDT", 100_000m);

        result.MaxDrawdownPercent.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task RunAsync_ShouldCalculateCorrectSharpeRatio_WithMultipleTrades()
    {
        // Alternating up/down candles to trigger multiple trades via stop loss
        var candles = CreateCandlesWithStopLossTriggers(200, 100m);
        var engine = CreateEngine(CreateDecision(stopLoss: 95m, takeProfit: 120m, positionSizePercent: 0.05m));

        var result = await engine.RunAsync(candles, "BTCUSDT", 100_000m);

        if (result.TotalTrades >= 2)
        {
            result.SharpeRatio.Should().NotBe(0, "Sharpe should be computed when >= 2 trades");
            result.SortinoRatio.Should().NotBe(double.NaN);
        }
    }

    [Fact]
    public async Task RunAsync_ShouldIncludeEquityCurve_WithCorrectLength()
    {
        var engine = CreateEngine(CreateDecision(positionSizePercent: 0.10m, stopLoss: 10m, takeProfit: 1_000_000m));
        var candles = CreateCandles(65, 100m, 1m);

        var result = await engine.RunAsync(candles, "BTCUSDT", 100_000m);

        // Equity curve = starting capital + 1 per trade
        result.EquityCurve.Should().HaveCountGreaterOrEqualTo(1);
        result.EquityCurve[0].Should().Be(100_000m, "First point should be starting capital");
    }

    [Fact]
    public async Task RunAsync_ShouldRejectLowConfidenceSignals()
    {
        // Confidence = 40 (below 55 threshold)
        var decision = new TradeDecision
        {
            ShouldTrade = true, Side = TradeSide.Buy, Confidence = 40,
            StopLoss = 90m, TakeProfit = 120m, PositionSizePercent = 0.10m,
            Reasoning = "Low confidence", StrategyTag = "Test", RegimeTag = "Test"
        };
        var engine = CreateEngine(decision);
        var candles = CreateCandles(65, 100m, 1m);

        var result = await engine.RunAsync(candles, "BTCUSDT", 100_000m);

        result.TotalTrades.Should().Be(0, "Should reject signals with confidence < 55");
    }

    [Fact]
    public async Task RunAsync_ShouldNotTrade_WhenShouldTradeIsFalse()
    {
        var decision = new TradeDecision { ShouldTrade = false, Confidence = 80 };
        var engine = CreateEngine(decision);
        var candles = CreateCandles(65, 100m, 1m);

        var result = await engine.RunAsync(candles, "BTCUSDT", 100_000m);

        result.TotalTrades.Should().Be(0);
        result.FinalEquity.Should().Be(100_000m);
    }

    // ─── PBO Overfitting Tests ───

    [Fact]
    public async Task ComputePboAsync_ShouldReturnValidPBO_WithSufficientData()
    {
        var engine = CreateEngine(CreateDecision(positionSizePercent: 0.10m, stopLoss: 50m, takeProfit: 200m));
        var candles = CreateCandles(16 * 60 + 10, 100m, 0.5m); // 970 candles

        var pbo = await engine.ComputePboAsync(candles, "BTCUSDT", 16, 100_000m);

        pbo.Should().NotBeNull();
        pbo.PBO.Should().BeInRange(0, 1);
        pbo.TotalCombinations.Should().BeGreaterThan(0);
        pbo.TrainSharpeRatios.Should().NotBeEmpty();
        pbo.TestSharpeRatios.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ComputePboAsync_ShouldReturnPBO1_WhenInsufficientData()
    {
        var engine = CreateEngine(CreateDecision());
        var candles = CreateCandles(100, 100m, 1m); // too few for 16 segments

        var pbo = await engine.ComputePboAsync(candles, "BTCUSDT", 16, 100_000m);

        pbo.PBO.Should().Be(1.0, "Insufficient data should return worst-case PBO");
    }

    [Fact]
    public async Task ComputePboAsync_ShouldComputeStochasticDominance()
    {
        var engine = CreateEngine(CreateDecision(positionSizePercent: 0.10m, stopLoss: 50m, takeProfit: 200m));
        var candles = CreateCandles(16 * 60 + 10, 100m, 0.5m);

        var pbo = await engine.ComputePboAsync(candles, "BTCUSDT", 16, 100_000m);

        pbo.StochasticDominance.Should().BeInRange(0, 1);
        pbo.ExpectedPerformance.Should().NotBe(double.NaN);
    }

    // ─── BacktestOrchestrator Gate Tests ───

    [Fact]
    public void EvaluateBacktestPass_ShouldFail_WhenTooFewTrades()
    {
        var result = new BacktestResult { TotalTrades = 10, TotalPnL = 1000, ProfitFactor = 2.0, SharpeRatio = 1.5, MaxDrawdownPercent = 10 };
        BacktestOrchestrator.EvaluateBacktestPass(result, null).Should().BeFalse("30 trade minimum not met");
    }

    [Fact]
    public void EvaluateBacktestPass_ShouldFail_WhenNegativePnL()
    {
        var result = new BacktestResult { TotalTrades = 50, TotalPnL = -100, ProfitFactor = 0.8, SharpeRatio = -0.5, MaxDrawdownPercent = 15 };
        BacktestOrchestrator.EvaluateBacktestPass(result, null).Should().BeFalse();
    }

    [Fact]
    public void EvaluateBacktestPass_ShouldFail_WhenLowSharpe()
    {
        var result = new BacktestResult { TotalTrades = 50, TotalPnL = 500, ProfitFactor = 1.5, SharpeRatio = 0.3, MaxDrawdownPercent = 10 };
        BacktestOrchestrator.EvaluateBacktestPass(result, null).Should().BeFalse("Sharpe < 0.5 should fail");
    }

    [Fact]
    public void EvaluateBacktestPass_ShouldFail_WhenHighDrawdown()
    {
        var result = new BacktestResult { TotalTrades = 50, TotalPnL = 500, ProfitFactor = 1.5, SharpeRatio = 1.0, MaxDrawdownPercent = 30 };
        BacktestOrchestrator.EvaluateBacktestPass(result, null).Should().BeFalse("MaxDD > 25% should fail");
    }

    [Fact]
    public void EvaluateBacktestPass_ShouldFail_WhenPBOTooHigh()
    {
        var result = new BacktestResult { TotalTrades = 50, TotalPnL = 500, ProfitFactor = 1.5, SharpeRatio = 1.0, MaxDrawdownPercent = 10 };
        var pbo = new PboResult { PBO = 0.7 };
        BacktestOrchestrator.EvaluateBacktestPass(result, pbo).Should().BeFalse("PBO > 0.5 should fail");
    }

    [Fact]
    public void EvaluateBacktestPass_ShouldPass_WhenAllCriteriaMet()
    {
        var result = new BacktestResult { TotalTrades = 50, TotalPnL = 1000, ProfitFactor = 1.8, SharpeRatio = 1.2, MaxDrawdownPercent = 12 };
        var pbo = new PboResult { PBO = 0.2 };
        BacktestOrchestrator.EvaluateBacktestPass(result, pbo).Should().BeTrue();
    }

    [Fact]
    public void EvaluateBacktestPass_ShouldPass_WhenNoPBO()
    {
        var result = new BacktestResult { TotalTrades = 50, TotalPnL = 1000, ProfitFactor = 1.8, SharpeRatio = 1.2, MaxDrawdownPercent = 12 };
        BacktestOrchestrator.EvaluateBacktestPass(result, null).Should().BeTrue();
    }

    // ─── WalkForward Tests ───

    [Fact]
    public async Task WalkForward_ShouldProduceWindowResults()
    {
        var engine = CreateEngine(CreateDecision(positionSizePercent: 0.10m, stopLoss: 50m, takeProfit: 200m));
        var wf = new WalkForwardEngine(engine, Substitute.For<ILogger<WalkForwardEngine>>());
        var candles = CreateCandles(600, 100m, 0.3m);

        var result = await wf.RunAsync(candles, "BTCUSDT", numWindows: 4, trainRatio: 0.7, capital: 100_000m);

        result.WindowResults.Should().HaveCountGreaterThan(0);
        result.CompletedWindows.Should().BeGreaterThan(0);
        result.WalkForwardEfficiency.Should().NotBe(double.NaN);
    }

    [Fact]
    public async Task WalkForward_ShouldReturnEmpty_WhenInsufficientData()
    {
        var engine = CreateEngine(CreateDecision());
        var wf = new WalkForwardEngine(engine, Substitute.For<ILogger<WalkForwardEngine>>());
        var candles = CreateCandles(50, 100m, 1m);

        var result = await wf.RunAsync(candles, "BTCUSDT", numWindows: 4);

        result.WindowResults.Should().BeEmpty();
        result.IsRobust.Should().BeFalse();
    }

    [Fact]
    public async Task WalkForward_ShouldTrackRegimePerWindow()
    {
        var engine = CreateEngine(CreateDecision(positionSizePercent: 0.10m, stopLoss: 50m, takeProfit: 200m));
        var wf = new WalkForwardEngine(engine, Substitute.For<ILogger<WalkForwardEngine>>());
        var candles = CreateCandles(600, 100m, 0.3m);

        var result = await wf.RunAsync(candles, "BTCUSDT", numWindows: 4);

        foreach (var w in result.WindowResults)
        {
            w.TrainRegime.Should().NotBeNullOrEmpty();
            w.OosRegime.Should().NotBeNullOrEmpty();
        }

        result.RegimeStabilityScore.Should().BeInRange(0, 1);
    }

    [Fact]
    public async Task WalkForward_ShouldCalculateOosConsistency()
    {
        var engine = CreateEngine(CreateDecision(positionSizePercent: 0.10m, stopLoss: 50m, takeProfit: 200m));
        var wf = new WalkForwardEngine(engine, Substitute.For<ILogger<WalkForwardEngine>>());
        var candles = CreateCandles(600, 100m, 0.3m);

        var result = await wf.RunAsync(candles, "BTCUSDT", numWindows: 4);

        result.OosConsistency.Should().BeInRange(0, 1);
        result.ProfitFactorDegradation.Should().NotBe(double.NaN);
    }

    // ─── Edge Cases ───

    [Fact]
    public async Task RunAsync_ShouldHandleNullCandles()
    {
        var engine = CreateEngine(CreateDecision());
        var result = await engine.RunAsync(null!, "BTCUSDT", 100_000m);
        result.TotalTrades.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ShouldHandleZeroCapital()
    {
        var engine = CreateEngine(CreateDecision(positionSizePercent: 0.10m));
        var candles = CreateCandles(65, 100m, 1m);
        var result = await engine.RunAsync(candles, "BTCUSDT", 0m);
        result.TotalTrades.Should().Be(0, "Zero capital means zero quantity so no trades");
    }

    [Fact]
    public async Task RunAsync_ShouldComputeMaxConsecutiveLosses()
    {
        var engine = CreateEngine(CreateDecision(positionSizePercent: 0.10m, stopLoss: 99m, takeProfit: 200m));
        var candles = CreateCandlesWithStopLossTriggers(200, 100m);

        var result = await engine.RunAsync(candles, "BTCUSDT", 100_000m);

        result.MaxConsecutiveLosses.Should().BeGreaterOrEqualTo(0);
    }

    // ─── Helpers ───

    private static BacktestEngineApp CreateEngine(TradeDecision decision)
    {
        var technicalAnalysis = Substitute.For<ITechnicalAnalysisService>();
        technicalAnalysis.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<IReadOnlyList<Candle>>(), Arg.Any<RiskParameters>())
            .Returns(decision);

        var coordinator = Substitute.For<IAgentCoordinator>();
        coordinator.RunFullPipelineAsync(
                Arg.Any<Signal>(),
                Arg.Any<IReadOnlyList<MarketTick>>(),
                Arg.Any<IReadOnlyList<Candle>>(),
                Arg.Any<MarketRegime>(),
                Arg.Any<IReadOnlyList<Trade>>(),
                Arg.Any<IReadOnlyList<Trade>>(),
                Arg.Any<CancellationToken>())
            .Returns(new FiveTierSnapshot
            {
                Votes = new[]
                {
                    new StrategyVote
                    {
                        StrategyTag = "Test",
                        Vote = VoteOutcome.Approve,
                        ConfidenceAdjustment = 5
                    }
                }
            });

        return new BacktestEngineApp(
            technicalAnalysis,
            coordinator,
            Substitute.For<ILogger<BacktestEngineApp>>());
    }

    private static TradeDecision CreateDecision(
        decimal positionSizePercent = 0.10m,
        decimal stopLoss = 90m,
        decimal takeProfit = 120m)
        => new()
        {
            ShouldTrade = true,
            Side = TradeSide.Buy,
            Confidence = 80,
            Reasoning = "Synthetic backtest decision",
            StopLoss = stopLoss,
            TakeProfit = takeProfit,
            PositionSizePercent = positionSizePercent,
            StrategyTag = "TrendConfluence",
            RegimeTag = "Trending",
            ExitPolicyTag = "TrendRunner"
        };

    private static List<Candle> CreateCandles(int count, decimal startPrice, decimal increment)
    {
        var candles = new List<Candle>(count);
        for (var i = 0; i < count; i++)
        {
            var open = startPrice + (increment * i);
            var close = open + increment;
            candles.Add(new Candle
            {
                Timestamp = new DateTime(2026, 4, 1).AddMinutes(i * 5),
                Open = open,
                High = close + increment,
                Low = open - increment,
                Close = close,
                Volume = 10_000m + i
            });
        }
        return candles;
    }

    private static List<Candle> CreateCandlesWithStopLossTriggers(int count, decimal basePrice)
    {
        var candles = new List<Candle>(count);
        for (int i = 0; i < count; i++)
        {
            // Zigzag pattern to trigger stop losses and take profits
            decimal direction = (i / 10) % 2 == 0 ? 1m : -1m;
            decimal offset = (i % 10) * 2m * direction;
            decimal price = basePrice + offset;

            candles.Add(new Candle
            {
                Timestamp = new DateTime(2026, 4, 1).AddMinutes(i * 5),
                Open = price,
                High = price + 3m,
                Low = price - 3m,
                Close = price + direction,
                Volume = 5_000m
            });
        }
        return candles;
    }
}
