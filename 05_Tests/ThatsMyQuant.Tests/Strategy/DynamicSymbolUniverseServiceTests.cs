using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class DynamicSymbolUniverseServiceTests
{
    [Fact]
    public async Task BuildUniverseAsync_RanksSymbolsByMarketAndExpectancy()
    {
        var marketData = Substitute.For<IMarketDataService>();
        marketData.GetCandlesAsync(Arg.Any<string>(), 24, "BIST", "1h")
            .Returns(call => Task.FromResult(BuildCandles(call.ArgAt<string>(0))));

        var gateway = Substitute.For<IBistMarketGateway>();
        gateway.GetUniverseAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["THYAO", "GARAN", "AKBNK"]));

        var sut = new DynamicSymbolUniverseService(
            marketData,
            NullLogger<DynamicSymbolUniverseService>.Instance,
            gateway);

        var result = await sut.BuildUniverseAsync("BIST", new[]
        {
            new Trade { Symbol = "THYAO", EntryPrice = 100m, ExitPrice = 110m, Quantity = 1m, Side = TradeSide.Buy, ExitTime = DateTime.UtcNow, Status = TradeStatus.Executed },
            new Trade { Symbol = "GARAN", EntryPrice = 100m, ExitPrice = 95m, Quantity = 1m, Side = TradeSide.Buy, ExitTime = DateTime.UtcNow, Status = TradeStatus.Executed }
        }, maxSymbols: 2);

        result.SelectedSymbols.Should().HaveCount(2);
        result.SelectedSymbols[0].Symbol.Should().Be("THYAO");
    }

    private static List<Candle> BuildCandles(string symbol)
    {
        var closes = symbol switch
        {
            "THYAO" => new[] { 100m, 102m, 103m, 104m, 106m, 108m },
            "GARAN" => new[] { 100m, 99m, 98m, 97m, 96m, 95m },
            _ => new[] { 100m, 100.2m, 100.4m, 100.6m, 100.8m, 101m }
        };

        return closes.Select((close, index) => new Candle
        {
            Timestamp = DateTime.UtcNow.AddHours(index),
            Open = close - 1m,
            High = close + 1m,
            Low = close - 1m,
            Close = close,
            Volume = symbol == "THYAO" ? 8_000_000m : 4_000_000m
        }).ToList();
    }
}
