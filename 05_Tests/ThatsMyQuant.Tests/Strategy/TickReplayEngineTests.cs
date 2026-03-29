using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class TickReplayEngineTests
{
    [Fact]
    public async Task ReplayAsync_ShouldUseOrderBookFillsWhenDepthSnapshotsAreAvailable()
    {
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
                Votes = [StrategyVote.Approve("TrendConfluence", 20f, "Replay approval")],
                RiskGauntlet = RiskGuardResult.Pass("TickReplay"),
                Meta = MetaAdaptation.Default
            });

        var engine = new TickReplayEngine(
            coordinator,
            new FeatureEngineeringAgent(),
            NullLogger<TickReplayEngine>.Instance);

        var ticks = Enumerable.Range(0, 170)
            .Select(index => new MarketTick(
                "BTCUSDT",
                100m + index * 0.05m,
                1.0m,
                1_710_000_000_000 + index * 100,
                false))
            .ToList();

        var candles = Enumerable.Range(0, 80)
            .Select(index => new Candle
            {
                Timestamp = DateTime.UtcNow.AddMinutes(index),
                Open = 100m,
                High = 103m,
                Low = 99m,
                Close = 101m + index * 0.01m,
                Volume = 1_000m
            })
            .ToList();

        var orderBooks = ticks
            .Select(tick => new OrderBookSnapshot(
                tick.Symbol,
                tick.TimestampMs,
                [new OrderBookLevel(tick.Price - 0.05m, 50m), new OrderBookLevel(tick.Price - 0.10m, 50m)],
                [new OrderBookLevel(tick.Price + 0.05m, 50m), new OrderBookLevel(tick.Price + 0.10m, 50m)]))
            .ToList();

        var result = await engine.ReplayAsync(ticks, candles, "BTCUSDT", 100_000m, orderBooks);

        result.Trades.Should().NotBeEmpty();
        result.Trades.Should().OnlyContain(trade => trade.ExecutionMode == "TICK-REPLAY");
        result.Trades[0].Reasoning.Should().Contain("OrderBook");
        result.Trades[0].EntryPrice.Should().BeGreaterThan(ticks[100].Price);
    }
}
