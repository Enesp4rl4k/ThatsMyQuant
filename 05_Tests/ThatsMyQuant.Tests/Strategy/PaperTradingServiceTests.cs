using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class PaperTradingServiceTests
{
    [Fact]
    public async Task ProcessSymbolAsync_ShouldTakePartialProfit_WhenLifecycleSupervisorTriggers()
    {
        MarketHoursScheduler.OverrideCanTrade = true;
        try
        {
            var analysis = Substitute.For<ITechnicalAnalysisService>();
            var marketData = Substitute.For<IMarketDataService>();
            var logger = Substitute.For<ILogger<PaperTradingService>>();

            marketData.GetCandlesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
                .Returns(CreateCandles(100m, 104.5m));

            var service = new PaperTradingService(
                analysis,
                marketData,
                logger,
                positionSupervisor: new PositionSupervisorService());

            service.Portfolio.OpenPositions.Add(new PaperPosition
            {
                Symbol = "THYAO",
                Side = TradeSide.Buy,
                EntryPrice = 100m,
                CurrentPrice = 100m,
                Quantity = 10m,
                RemainingQuantity = 10m,
                StopLoss = 97m,
                TakeProfit = 110m,
                PartialTakeProfit = 104m,
                PartialExitFraction = 0.40m,
                ExitPolicyTag = "TrendRunner",
                BreakevenTriggerFraction = 0.55m,
                MaxHoldingBars = 18,
                ExecutionMode = "PAPER",
                PromotionStage = "Paper",
                EntryTime = DateTime.UtcNow.AddMinutes(-20)
            });

            var initialQuantity = service.Portfolio.OpenPositions[0].Quantity;
            var partialResult = await service.ProcessSymbolAsync("THYAO");

            partialResult.Should().Contain("PARTIAL");
            service.Portfolio.OpenPositions.Should().ContainSingle();
            service.Portfolio.OpenPositions[0].HasTakenPartialProfit.Should().BeTrue();
            service.Portfolio.OpenPositions[0].Quantity.Should().BeLessThan(initialQuantity);
            service.Portfolio.OpenPositions[0].RealizedPnL.Should().BeGreaterThan(0m);
        }
        finally
        {
            MarketHoursScheduler.OverrideCanTrade = null;
        }
    }

    private static List<Candle> CreateCandles(decimal basePrice, decimal lastClose)
    {
        var candles = new List<Candle>();
        var start = DateTime.UtcNow.AddHours(-79);

        for (var i = 0; i < 80; i++)
        {
            var close = i == 79 ? lastClose : basePrice + (i * 0.03m);
            var open = close - 0.2m;
            candles.Add(new Candle
            {
                Timestamp = start.AddHours(i),
                Open = open,
                High = close + 0.5m,
                Low = open - 0.4m,
                Close = close,
                Volume = 1_500_000 + (i * 10_000)
            });
        }

        return candles;
    }
}
