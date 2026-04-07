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

    private static TradeDecision CreateDecision(decimal positionSizePercent, decimal stopLoss, decimal takeProfit)
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
}
