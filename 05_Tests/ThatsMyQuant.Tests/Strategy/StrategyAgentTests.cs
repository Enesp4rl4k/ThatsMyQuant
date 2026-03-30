using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using ThatsMyQuant.Infrastructure.Services.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

file static class StrategyAgentTestData
{
    public static Signal Signal(decimal price = 100m, TradeSide side = TradeSide.Buy, string symbol = "BTCUSDT")
        => new()
        {
            Symbol = symbol,
            Side = side,
            Price = price,
            Timestamp = new DateTime(2026, 03, 31, 10, 0, 0, DateTimeKind.Utc)
        };

    public static FoundationSnapshot Foundation(decimal vwap = 0m, double imbalance = 0, double spreadBps = 0)
        => new()
        {
            Vwap = vwap,
            BidAskImbalance = imbalance,
            SpreadBps = spreadBps
        };

    public static PatternSnapshot Patterns(bool squeeze = false, double vwapDeviation = 0)
        => new()
        {
            BollingerSqueeze = squeeze,
            VwapDeviationSigmas = vwapDeviation
        };

    public static List<Candle> LinearCandles(int count, decimal startClose, decimal endClose, decimal volume = 1_000m)
    {
        var candles = new List<Candle>(count);
        for (var i = 0; i < count; i++)
        {
            var t = count > 1 ? (decimal)i / (count - 1) : 0m;
            var close = startClose + (endClose - startClose) * t;
            candles.Add(new Candle
            {
                Timestamp = DateTime.UtcNow.AddMinutes(-count + i),
                Open = close - 0.5m,
                High = close + 1m,
                Low = close - 1m,
                Close = close,
                Volume = volume
            });
        }

        return candles;
    }

    public static List<Candle> FlatCandles(int count, decimal close, decimal volume = 1_000m)
        => LinearCandles(count, close, close, volume);

    public static List<Candle> BreakoutCandles()
    {
        var candles = new List<Candle>();
        var time = DateTime.UtcNow.AddMinutes(-60);

        for (var i = 0; i < 44; i++)
        {
            var close = 103m + ((i % 4) - 1.5m) * 0.8m;
            candles.Add(new Candle
            {
                Timestamp = time.AddMinutes(i),
                Open = close - 0.2m,
                High = close + 0.8m,
                Low = close - 0.8m,
                Close = close,
                Volume = 1_000m
            });
        }

        for (var i = 44; i < 49; i++)
        {
            var close = 104.4m + (i - 44) * 0.08m;
            candles.Add(new Candle
            {
                Timestamp = time.AddMinutes(i),
                Open = close - 0.15m,
                High = close + 0.35m,
                Low = close - 0.35m,
                Close = close,
                Volume = 950m
            });
        }

        candles.Add(new Candle
        {
            Timestamp = time.AddMinutes(49),
            Open = 101.0m,
            High = 110m,
            Low = 100.7m,
            Close = 109m,
            Volume = 4_000m
        });

        return candles;
    }

    public static List<Candle> MeanRevertingCandles(int count, decimal center, decimal amplitude)
    {
        var candles = new List<Candle>(count);
        for (var i = 0; i < count; i++)
        {
            var phase = (decimal)Math.Sin(i * 0.7);
            var close = center + (phase * amplitude);
            candles.Add(new Candle
            {
                Timestamp = DateTime.UtcNow.AddMinutes(-count + i),
                Open = close,
                High = close + 0.7m,
                Low = close - 0.7m,
                Close = close,
                Volume = 1_200m
            });
        }

        return candles;
    }
}

public class MomentumStrategyAgentTests
{
    private readonly MomentumStrategyAgent _agent = new(
        Substitute.For<ILogger<MomentumStrategyAgent>>(),
        Options.Create(new MomentumOptions()));

    [Theory]
    [InlineData(MarketRegime.Trending, true)]
    [InlineData(MarketRegime.Ranging, false)]
    [InlineData(MarketRegime.Volatile, false)]
    public void CanHandle_MatchesTrendingOnly(MarketRegime regime, bool expected)
    {
        _agent.CanHandle(StrategyAgentTestData.Signal(), regime).Should().Be(expected);
    }

    [Fact]
    public async Task Evaluate_Abstains_WhenTooFewCandles()
    {
        var result = await _agent.EvaluateAsync(
            StrategyAgentTestData.Signal(),
            PatternSnapshot.Empty,
            FoundationSnapshot.Empty,
            StrategyAgentTestData.FlatCandles(10, 100m));

        result.Vote.Should().Be(VoteOutcome.Abstain);
    }

    [Fact]
    public async Task Evaluate_Approves_BullishConfluence()
    {
        var candles = StrategyAgentTestData.LinearCandles(60, 80m, 120m, 1_200m);
        for (var i = candles.Count - 3; i < candles.Count; i++)
            candles[i].Volume = 3_000m;

        var result = await _agent.EvaluateAsync(
            StrategyAgentTestData.Signal(120m),
            PatternSnapshot.Empty,
            StrategyAgentTestData.Foundation(vwap: 100m),
            candles);

        result.Vote.Should().Be(VoteOutcome.Approve);
        result.Reasoning.Should().Contain("Bullish momentum");
        result.Reasoning.Should().Contain("ADX=");
    }

    [Fact]
    public async Task Evaluate_Rejects_BearishConfluence()
    {
        var candles = StrategyAgentTestData.LinearCandles(60, 120m, 80m, 1_200m);
        for (var i = candles.Count - 3; i < candles.Count; i++)
            candles[i].Volume = 3_000m;

        var result = await _agent.EvaluateAsync(
            StrategyAgentTestData.Signal(80m, side: TradeSide.Sell),
            PatternSnapshot.Empty,
            StrategyAgentTestData.Foundation(vwap: 100m),
            candles);

        result.Vote.Should().Be(VoteOutcome.Reject);
        result.Reasoning.Should().Contain("Bearish momentum");
    }
}

public class BreakoutStrategyAgentTests
{
    private readonly BreakoutStrategyAgent _agent = new(
        Substitute.For<ILogger<BreakoutStrategyAgent>>(),
        Options.Create(new BreakoutOptions
        {
            LookbackBars = 40,
            MinConsolidationBars = 5
        }));

    [Fact]
    public async Task Evaluate_Approves_LongBreakoutWithConfluence()
    {
        var candles = StrategyAgentTestData.BreakoutCandles();
        var result = await _agent.EvaluateAsync(
            StrategyAgentTestData.Signal(109m),
            PatternSnapshot.Empty,
            StrategyAgentTestData.Foundation(imbalance: 0.42),
            candles);

        result.Vote.Should().Be(VoteOutcome.Approve);
        result.Reasoning.Should().Contain("Breakout");
        result.Reasoning.Should().Contain("vol surge");
    }

    [Fact]
    public async Task Evaluate_Abstains_WithoutVolumeSurge()
    {
        var candles = StrategyAgentTestData.BreakoutCandles();
        candles[^1].Volume = 1_000m;

        var result = await _agent.EvaluateAsync(
            StrategyAgentTestData.Signal(109m),
            PatternSnapshot.Empty,
            StrategyAgentTestData.Foundation(imbalance: 0.42),
            candles);

        result.Vote.Should().Be(VoteOutcome.Abstain);
    }
}

public class ScalpingStrategyAgentTests
{
    private readonly ScalpingStrategyAgent _agent = new(
        Substitute.For<ILogger<ScalpingStrategyAgent>>(),
        Options.Create(new ScalpingOptions()));

    [Fact]
    public async Task Evaluate_Abstains_WhenOfiIsTooWeak()
    {
        var result = await _agent.EvaluateAsync(
            StrategyAgentTestData.Signal(100m),
            StrategyAgentTestData.Patterns(squeeze: true, vwapDeviation: -0.4),
            StrategyAgentTestData.Foundation(imbalance: 0.20, spreadBps: 4),
            StrategyAgentTestData.FlatCandles(20, 100m));

        result.Vote.Should().Be(VoteOutcome.Abstain);
    }

    [Fact]
    public async Task Evaluate_Approves_LongScalp_WhenMicrostructureAligns()
    {
        var signal = StrategyAgentTestData.Signal(100m);
        signal.BestBid = 99.98;
        signal.BestAsk = 100.02;

        var result = await _agent.EvaluateAsync(
            signal,
            StrategyAgentTestData.Patterns(squeeze: true, vwapDeviation: -0.6),
            StrategyAgentTestData.Foundation(imbalance: 0.82),
            StrategyAgentTestData.FlatCandles(20, 100m));

        result.Vote.Should().Be(VoteOutcome.Approve);
        result.Reasoning.Should().Contain("Scalp long");
    }

    [Fact]
    public async Task Evaluate_Rejects_ShortScalp_WhenBearishFlowAligns()
    {
        var signal = StrategyAgentTestData.Signal(100m, side: TradeSide.Sell);
        signal.BestBid = 99.97;
        signal.BestAsk = 100.03;

        var result = await _agent.EvaluateAsync(
            signal,
            StrategyAgentTestData.Patterns(squeeze: true, vwapDeviation: 0.6),
            StrategyAgentTestData.Foundation(imbalance: -0.85),
            StrategyAgentTestData.FlatCandles(20, 100m));

        result.Vote.Should().Be(VoteOutcome.Reject);
        result.Reasoning.Should().Contain("Scalp short");
    }
}

public class StatisticalArbitrageAgentTests
{
    private readonly StatisticalArbitrageAgent _agent = new(
        Substitute.For<ILogger<StatisticalArbitrageAgent>>(),
        Options.Create(new StatArbOptions
        {
            EntryZScore = 1.5,
            MaxHalfLife = 30,
            AdfCriticalValue = 0.2
        }));

    [Theory]
    [InlineData(MarketRegime.Ranging, true)]
    [InlineData(MarketRegime.Trending, false)]
    [InlineData(MarketRegime.Volatile, false)]
    public void CanHandle_MatchesRangingOnly(MarketRegime regime, bool expected)
    {
        _agent.CanHandle(StrategyAgentTestData.Signal(), regime).Should().Be(expected);
    }

    [Fact]
    public async Task Evaluate_Abstains_WhenDeviationIsTooSmall()
    {
        var candles = StrategyAgentTestData.MeanRevertingCandles(60, 100m, 1.2m);
        var signal = StrategyAgentTestData.Signal(100.5m);

        var result = await _agent.EvaluateAsync(
            signal,
            PatternSnapshot.Empty,
            FoundationSnapshot.Empty,
            candles);

        result.Vote.Should().Be(VoteOutcome.Abstain);
    }

    [Fact]
    public async Task Evaluate_Approves_LongMeanReversion_WhenPriceIsDeepBelowMean()
    {
        var candles = StrategyAgentTestData.MeanRevertingCandles(60, 100m, 2.5m);
        var signal = StrategyAgentTestData.Signal(94m);

        var result = await _agent.EvaluateAsync(
            signal,
            StrategyAgentTestData.Patterns(vwapDeviation: -0.8),
            FoundationSnapshot.Empty,
            candles);

        result.Vote.Should().Be(VoteOutcome.Approve);
        result.Reasoning.Should().Contain("StatArb long");
    }

    [Fact]
    public async Task Evaluate_Approves_ShortMeanReversion_WhenPriceIsFarAboveMean()
    {
        var candles = StrategyAgentTestData.MeanRevertingCandles(60, 100m, 2.5m);
        var signal = StrategyAgentTestData.Signal(106m, side: TradeSide.Sell);

        var result = await _agent.EvaluateAsync(
            signal,
            StrategyAgentTestData.Patterns(vwapDeviation: 0.8),
            FoundationSnapshot.Empty,
            candles);

        result.Vote.Should().Be(VoteOutcome.Approve);
        result.Reasoning.Should().Contain("StatArb short");
    }
}
