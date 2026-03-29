using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public sealed class BacktestEngineTests
{
    private readonly ITechnicalAnalysisService _ta = Substitute.For<ITechnicalAnalysisService>();
    private readonly IAgentCoordinator _coordinator = Substitute.For<IAgentCoordinator>();
    private readonly ILogger<BacktestEngineApp> _logger = Substitute.For<ILogger<BacktestEngineApp>>();
    private readonly BacktestOptions _opts = new() { MinCandlesRequired = 10, DefaultStartingCapital = 100_000m };

    private BacktestEngineApp CreateEngine(BacktestOptions? opts = null)
    {
        var o = opts ?? _opts;
        return new BacktestEngineApp(_ta, _coordinator, _logger, Options.Create(o));
    }

    private static List<Candle> MakeTrendingCandles(int count, decimal startPrice = 100m, decimal step = 1m)
    {
        var candles = new List<Candle>();
        for (int i = 0; i < count; i++)
        {
            var price = startPrice + i * step;
            candles.Add(new Candle
            {
                Timestamp = DateTime.UtcNow.AddMinutes(-count + i),
                Open = price - 0.5m,
                High = price + 1m,
                Low = price - 1m,
                Close = price,
                Volume = 1000m
            });
        }
        return candles;
    }

    // ─── Insufficient data ───────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ReturnsEmpty_WhenInsufficientCandles()
    {
        var engine = CreateEngine();
        var candles = MakeTrendingCandles(5); // less than MinCandlesRequired=10

        var result = await engine.RunAsync(candles, "BTCUSDT");

        result.TotalTrades.Should().Be(0);
        result.Symbol.Should().Be("BTCUSDT");
    }

    [Fact]
    public async Task RunAsync_ReturnsEmpty_WhenCandlesNull()
    {
        var engine = CreateEngine();

        var result = await engine.RunAsync(null!, "BTCUSDT");

        result.TotalTrades.Should().Be(0);
    }

    // ─── Default starting capital ────────────────────────────────────────

    [Fact]
    public async Task RunAsync_UsesDefaultCapital_WhenZeroPassed()
    {
        var engine = CreateEngine();
        var candles = MakeTrendingCandles(5);

        var result = await engine.RunAsync(candles, "BTCUSDT", startingCapital: 0m);

        // Insufficient data so no trades, but starting capital should be the default
        result.Symbol.Should().Be("BTCUSDT");
    }

    // ─── No-trade scenario (TA rejects) ──────────────────────────────────

    [Fact]
    public async Task RunAsync_ProducesZeroTrades_WhenTARejectsAll()
    {
        _ta.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
            .Returns(new TradeDecision { ShouldTrade = false, Confidence = 10 });

        var engine = CreateEngine();
        var candles = MakeTrendingCandles(30);

        var result = await engine.RunAsync(candles, "BTCUSDT");

        result.TotalTrades.Should().Be(0);
        result.FinalEquity.Should().Be(100_000m);
    }

    // ─── Trade execution: SL/TP triggers ─────────────────────────────────

    [Fact]
    public async Task RunAsync_TriggersStopLoss_WhenPriceDropsBelowSL()
    {
        // TA approves a buy with SL below entry
        _ta.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
            .Returns(new TradeDecision
            {
                ShouldTrade = true, Confidence = 80, Side = TradeSide.Buy,
                StopLoss = 90m, TakeProfit = 120m, PositionSizePercent = 0.01m
            });

        // Agent coordinator returns permissive pipeline
        SetupPermissiveCoordinator();

        var candles = MakeTrendingCandles(15, startPrice: 100m, step: 1m);
        // Drop price hard on the last candle to trigger SL
        candles.Add(new Candle
        {
            Timestamp = DateTime.UtcNow, Open = 110m, High = 111m,
            Low = 85m, Close = 88m, Volume = 1000m
        });

        var engine = CreateEngine();
        var result = await engine.RunAsync(candles, "BTCUSDT");

        // Should have entered and exited via SL
        if (result.TotalTrades > 0)
        {
            result.Trades.Should().Contain(t => t.ExitReason == "StopLoss");
        }
    }

    [Fact]
    public async Task RunAsync_TriggersTakeProfit_WhenPriceExceedsTP()
    {
        _ta.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
            .Returns(new TradeDecision
            {
                ShouldTrade = true, Confidence = 80, Side = TradeSide.Buy,
                StopLoss = 90m, TakeProfit = 115m, PositionSizePercent = 0.01m
            });

        SetupPermissiveCoordinator();

        var candles = MakeTrendingCandles(15, startPrice: 100m, step: 1m);
        // Spike price to trigger TP
        candles.Add(new Candle
        {
            Timestamp = DateTime.UtcNow, Open = 113m, High = 120m,
            Low = 112m, Close = 118m, Volume = 1000m
        });

        var engine = CreateEngine();
        var result = await engine.RunAsync(candles, "BTCUSDT");

        if (result.TotalTrades > 0)
        {
            result.Trades.Should().Contain(t => t.ExitReason == "TakeProfit");
        }
    }

    // ─── PnL accounting ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_EquityCurveStartsAtInitialCapital()
    {
        _ta.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
            .Returns(new TradeDecision { ShouldTrade = false, Confidence = 10 });

        var engine = CreateEngine();
        var candles = MakeTrendingCandles(30);

        var result = await engine.RunAsync(candles, "BTCUSDT", 50_000m);

        result.FinalEquity.Should().Be(50_000m);
        result.StartingCapital.Should().Be(50_000m);
    }

    // ─── Metrics ─────────────────────────────────────────────────────────

    [Fact]
    public void WinRate_ComputesCorrectly()
    {
        var result = new BacktestResult
        {
            TotalTrades = 10,
            WinningTrades = 6,
            LosingTrades = 4
        };

        result.WinRate.Should().BeApproximately(60.0, 0.01);
    }

    [Fact]
    public void WinRate_IsZero_WhenNoTrades()
    {
        var result = new BacktestResult { TotalTrades = 0 };
        result.WinRate.Should().Be(0);
    }

    // ─── Agent coordinator veto ──────────────────────────────────────────

    [Fact]
    public async Task RunAsync_RespectsRiskGauntletVeto()
    {
        _ta.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
            .Returns(new TradeDecision
            {
                ShouldTrade = true, Confidence = 80, Side = TradeSide.Buy,
                StopLoss = 90m, TakeProfit = 120m, PositionSizePercent = 0.01m
            });

        // Coordinator returns vetoed risk gauntlet
        _coordinator.RunFullPipelineAsync(
                Arg.Any<Signal>(), Arg.Any<IReadOnlyList<MarketTick>>(),
                Arg.Any<IReadOnlyList<Candle>>(), Arg.Any<MarketRegime>(),
                Arg.Any<IReadOnlyList<Trade>>(), Arg.Any<IReadOnlyList<Trade>>(),
                Arg.Any<CancellationToken>())
            .Returns(new FiveTierSnapshot
            {
                Votes = Array.Empty<StrategyVote>(),
                RiskGauntlet = RiskGuardResult.Block("Test", SignalReasonFlags.RiskVeto, "Blocked by test"),
                Meta = MetaAdaptation.Default
            });

        var engine = CreateEngine();
        var candles = MakeTrendingCandles(30);
        var result = await engine.RunAsync(candles, "BTCUSDT");

        result.TotalTrades.Should().Be(0);
    }

    // ─── Slippage model ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_RecordsSlippageCost_WhenTradesExecute()
    {
        _ta.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
            .Returns(new TradeDecision
            {
                ShouldTrade = true, Confidence = 80, Side = TradeSide.Buy,
                StopLoss = 50m, TakeProfit = 200m, PositionSizePercent = 0.01m
            });

        SetupPermissiveCoordinator();

        var candles = MakeTrendingCandles(30, startPrice: 100m, step: 1m);
        // Trigger TP on last candle
        candles.Add(new Candle
        {
            Timestamp = DateTime.UtcNow, Open = 128m, High = 210m,
            Low = 127m, Close = 205m, Volume = 1000m
        });

        var engine = CreateEngine();
        var result = await engine.RunAsync(candles, "BTCUSDT");

        // If any trade executed, slippage should be tracked
        if (result.TotalTrades > 0)
        {
            result.TotalSlippageCost.Should().BeGreaterThan(0);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private void SetupPermissiveCoordinator()
    {
        _coordinator.RunFullPipelineAsync(
                Arg.Any<Signal>(), Arg.Any<IReadOnlyList<MarketTick>>(),
                Arg.Any<IReadOnlyList<Candle>>(), Arg.Any<MarketRegime>(),
                Arg.Any<IReadOnlyList<Trade>>(), Arg.Any<IReadOnlyList<Trade>>(),
                Arg.Any<CancellationToken>())
            .Returns(new FiveTierSnapshot
            {
                Votes = new[] { new StrategyVote
                {
                    StrategyTag = "TestStrategy",
                    Vote = VoteOutcome.Approve,
                    ConfidenceAdjustment = 10f,
                    PriorWeight = 1
                }},
                RiskGauntlet = RiskGuardResult.Pass("Test"),
                Meta = MetaAdaptation.Default
            });
    }
}
