using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Infrastructure;

public class BorsaMarketDataServiceTests
{
    [Fact]
    public async Task GetLivePriceAsync_ForBist_DelegatesToGateway()
    {
        var gateway = Substitute.For<IBistMarketGateway>();
        gateway.GetLivePriceAsync("THYAO", Arg.Any<CancellationToken>()).Returns(284.20m);

        var sut = new BorsaMarketDataService(NullLogger<BorsaMarketDataService>.Instance, gateway);

        var result = await sut.GetLivePriceAsync("THYAO", "BIST");

        result.Should().Be(284.20m);
        await gateway.Received(1).GetLivePriceAsync("THYAO", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCandlesAsync_ForBist_DelegatesToGateway()
    {
        var gateway = Substitute.For<IBistMarketGateway>();
        gateway.GetCandlesAsync("THYAO", 10, "1h", Arg.Any<CancellationToken>())
            .Returns(new List<Candle> { new() { Close = 1m, Open = 1m, High = 1m, Low = 1m, Volume = 1m, Timestamp = DateTime.UtcNow } });

        var sut = new BorsaMarketDataService(NullLogger<BorsaMarketDataService>.Instance, gateway);

        var result = await sut.GetCandlesAsync("THYAO", 10, "BIST", "1h");

        result.Should().HaveCount(1);
        await gateway.Received(1).GetCandlesAsync("THYAO", 10, "1h", Arg.Any<CancellationToken>());
    }
}
