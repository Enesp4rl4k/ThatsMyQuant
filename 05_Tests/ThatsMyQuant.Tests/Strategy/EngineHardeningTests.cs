using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Options;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class EngineHardeningTests
{
    [Fact]
    public async Task AnalyzeAsync_ShouldRejectCryptoTrade_InDeadZoneSession()
    {
        var logger = Substitute.For<ILogger<QuantIndicatorService>>();
        var service = new QuantIndicatorService(logger);
        var candles = CreateBullishCandles(new DateTime(2026, 3, 20, 18, 0, 0, DateTimeKind.Utc));

        var decision = await service.AnalyzeAsync(
            new Signal { Symbol = "BTC-USD", Price = candles[^1].Close },
            candles,
            RiskParameters.Default);

        decision.ShouldTrade.Should().BeFalse();
        decision.Reasoning.Should().Contain("Session gate blocked");
    }

    [Fact]
    public async Task EvaluatePortfolioGuardrailsAsync_ShouldRejectAfterThreeConsecutiveLosses()
    {
        var logger = Substitute.For<ILogger<RiskAgent>>();
        var agent = new RiskAgent(logger);
        var now = DateTime.UtcNow;
        var recentTrades = new[]
        {
            new Trade { Symbol = "THYAO", Side = TradeSide.Buy, EntryPrice = 100m, ExitPrice = 96m, Quantity = 1, ExitTime = now.AddHours(-1), Status = TradeStatus.Executed },
            new Trade { Symbol = "THYAO", Side = TradeSide.Buy, EntryPrice = 101m, ExitPrice = 97m, Quantity = 1, ExitTime = now.AddHours(-2), Status = TradeStatus.Executed },
            new Trade { Symbol = "THYAO", Side = TradeSide.Buy, EntryPrice = 102m, ExitPrice = 98m, Quantity = 1, ExitTime = now.AddHours(-3), Status = TradeStatus.Executed }
        };

        var result = await agent.EvaluatePortfolioGuardrailsAsync(
            recentTrades,
            new Signal { Symbol = "THYAO" },
            0.01m,
            RiskParameters.Default);

        result.Approved.Should().BeFalse();
        result.Reason.Should().Contain("Loss-streak guard");
    }

    [Fact]
    public async Task EvaluateAsync_ShouldReduceSize_WhenExecutionQualitySoftens()
    {
        var agent = new RiskAgent(Substitute.For<ILogger<RiskAgent>>());
        var now = DateTime.UtcNow;
        var signal = new Signal { Symbol = "BTCUSDT" };
        var recentTrades = new[]
        {
            CreateClosedTrade("BTCUSDT", now.AddHours(-1), pnl: 8m, slippagePercent: 0.18m, latencyMs: 540, venueBias: 0.93m),
            CreateClosedTrade("ETHUSDT", now.AddHours(-2), pnl: 6m, slippagePercent: 0.16m, latencyMs: 520, venueBias: 0.94m),
            CreateClosedTrade("BTCUSDT", now.AddHours(-3), pnl: 5m, slippagePercent: 0.17m, latencyMs: 560, venueBias: 0.92m),
            CreateClosedTrade("SOLUSDT", now.AddHours(-4), pnl: 4m, slippagePercent: 0.19m, latencyMs: 500, venueBias: 0.93m)
        };

        var result = await agent.EvaluateAsync(
            new TradeDecision
            {
                ShouldTrade = true,
                Side = TradeSide.Buy,
                Confidence = 86,
                PositionSizePercent = 0.05m
            },
            RiskParameters.Default,
            symbol24hVolume: 5_000_000m,
            recentTrades,
            signal);

        result.Approved.Should().BeTrue();
        result.SafePositionSizePercent.Should().BeLessThan(0.05m);
        result.SafePositionSizePercent.Should().BeGreaterThan(0.01m);
    }

    [Fact]
    public async Task EvaluateAsync_ShouldClipSizeToRemainingPortfolioHeadroom()
    {
        var agent = new RiskAgent(Substitute.For<ILogger<RiskAgent>>());
        var signal = new Signal { Symbol = "GARAN" };
        var openTrades = new[]
        {
            new Trade { Symbol = "THYAO", Status = TradeStatus.Executed, EntryPrice = 100m, Quantity = 1, PositionSizePercent = 0.14m, EntryTime = DateTime.UtcNow.AddMinutes(-30) },
            new Trade { Symbol = "SISE", Status = TradeStatus.Executed, EntryPrice = 100m, Quantity = 1, PositionSizePercent = 0.15m, EntryTime = DateTime.UtcNow.AddMinutes(-20) }
        };

        var guardrail = await agent.EvaluatePortfolioGuardrailsAsync(
            openTrades,
            signal,
            requestedPositionFraction: 0.05m,
            RiskParameters.Default);

        var size = await agent.EvaluateAsync(
            new TradeDecision
            {
                ShouldTrade = true,
                Side = TradeSide.Buy,
                Confidence = 84,
                PositionSizePercent = 0.05m
            },
            RiskParameters.Default,
            symbol24hVolume: 3_000_000m,
            openTrades,
            signal);

        guardrail.Approved.Should().BeTrue();
        size.Approved.Should().BeTrue();
        size.SafePositionSizePercent.Should().BeApproximately(0.01m, 0.0001m);
    }

    [Fact]
    public async Task EvaluatePortfolioGuardrailsAsync_ShouldRejectWhenCorrelatedBookIsAlreadyOverCap()
    {
        var agent = new RiskAgent(Substitute.For<ILogger<RiskAgent>>());
        var openTrades = new[]
        {
            new Trade { Symbol = "BTCUSDT", Status = TradeStatus.Executed, EntryPrice = 100m, Quantity = 1, PositionSizePercent = 0.10m, EntryTime = DateTime.UtcNow.AddMinutes(-45) },
            new Trade { Symbol = "ETHUSDT", Status = TradeStatus.Executed, EntryPrice = 100m, Quantity = 1, PositionSizePercent = 0.10m, EntryTime = DateTime.UtcNow.AddMinutes(-30) }
        };

        var result = await agent.EvaluatePortfolioGuardrailsAsync(
            openTrades,
            new Signal { Symbol = "SOLUSDT" },
            requestedPositionFraction: 0.02m,
            RiskParameters.Default);

        result.Approved.Should().BeFalse();
        result.Reason.Should().Contain("Correlated exposure");
    }

    [Fact]
    public async Task EvaluateAsync_ShouldReduceSize_WhenPortfolioIsInSoftDrawdown()
    {
        var options = Options.Create(new RiskGovernorOptions
        {
            SoftDrawdownPercent = 4m,
            HardDrawdownPercent = 8m
        });
        var agent = new RiskAgent(Substitute.For<ILogger<RiskAgent>>(), options: options);
        var now = DateTime.UtcNow;
        var signal = new Signal { Symbol = "THYAO" };
        var recentTrades = new[]
        {
            CreateClosedTrade("THYAO", now.AddHours(-4), pnl: 120m),
            CreateClosedTrade("THYAO", now.AddHours(-3), pnl: -3_500m),
            CreateClosedTrade("THYAO", now.AddHours(-2), pnl: -2_800m),
            CreateClosedTrade("THYAO", now.AddHours(-1), pnl: -900m)
        };

        var result = await agent.EvaluateAsync(
            new TradeDecision
            {
                ShouldTrade = true,
                Side = TradeSide.Buy,
                Confidence = 88,
                PositionSizePercent = 0.05m
            },
            RiskParameters.Default,
            symbol24hVolume: 2_000_000m,
            recentTrades,
            signal);

        result.Approved.Should().BeTrue();
        result.SafePositionSizePercent.Should().BeLessThan(0.05m);
        result.Reason.Should().Contain("drawdown=");
    }

    [Fact]
    public async Task ProcessSymbolAsync_ShouldSkipCryptoOutsidePreferredTradeWindow()
    {
        var analysis = Substitute.For<ITechnicalAnalysisService>();
        var marketData = Substitute.For<IMarketDataService>();
        var logger = Substitute.For<ILogger<PaperTradingService>>();

        marketData.GetCandlesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
            .Returns(CreateBullishCandles(new DateTime(2026, 3, 20, 18, 0, 0, DateTimeKind.Utc)));
        analysis.AnalyzeAsync(Arg.Any<Signal>(), Arg.Any<List<Candle>>(), Arg.Any<RiskParameters>())
            .Returns(new TradeDecision
            {
                ShouldTrade = true,
                Side = TradeSide.Buy,
                Confidence = 82,
                StopLoss = 95m,
                TakeProfit = 115m,
                PositionSizePercent = 0.03m,
                StrategyTag = "TrendConfluence",
                RegimeTag = "Trending"
            });

        var service = new PaperTradingService(analysis, marketData, logger);

        var result = await service.ProcessSymbolAsync("BTC-USD");

        result.Should().Contain("outside preferred trade window");
    }

    private static List<Candle> CreateBullishCandles(DateTime lastTimestampUtc)
    {
        var candles = new List<Candle>();
        var start = lastTimestampUtc.AddHours(-79);
        decimal price = 100m;

        for (var i = 0; i < 80; i++)
        {
            var drift = i < 55 ? 0.65m : 0.35m;
            var open = price;
            var close = price + drift;
            candles.Add(new Candle
            {
                Timestamp = start.AddHours(i),
                Open = open,
                High = close + 0.8m,
                Low = open - 0.5m,
                Close = close,
                Volume = 2_500_000 + (i * 40_000)
            });
            price = close;
        }

        return candles;
    }

    private static Trade CreateClosedTrade(
        string symbol,
        DateTime exitTime,
        decimal pnl,
        decimal slippagePercent = 0.05m,
        long latencyMs = 120,
        decimal venueBias = 1.0m)
    {
        var entryPrice = 100m;
        var exitPrice = entryPrice + pnl;

        return new Trade
        {
            Symbol = symbol,
            Side = TradeSide.Buy,
            EntryPrice = entryPrice,
            ExitPrice = exitPrice,
            Quantity = 1m,
            Status = TradeStatus.Executed,
            ExitTime = exitTime,
            ExecutionMode = "REAL",
            PromotionStage = "ScaledLive",
            EntrySlippagePercent = slippagePercent,
            ExecutionLatencyMs = latencyMs,
            VenueExecutionBias = venueBias
        };
    }
}
