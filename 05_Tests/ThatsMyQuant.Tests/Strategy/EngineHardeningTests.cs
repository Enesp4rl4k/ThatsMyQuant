using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
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
}
