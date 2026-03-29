using FluentAssertions;
using Xunit;
using ThatsMyQuant.Infrastructure.Services;

namespace ThatsMyQuant.Tests.Infrastructure;

public class OkxExecutionServiceTests
{
    [Theory]
    [InlineData("BTCUSDT", "BTC-USDT")]
    [InlineData("ETHUSDT", "ETH-USDT")]
    [InlineData("SOLUSDC", "SOL-USDC")]
    [InlineData("ETHBTC", "ETH-BTC")]
    [InlineData("DOGEETH", "DOGE-ETH")]
    [InlineData("USDTTRY", "USDT-TRY")]
    [InlineData("AVAXEUR", "AVAX-EUR")]
    public void NormalizeToOkx_ShouldConvertStandardSymbols(string input, string expected)
    {
        OkxExecutionService.NormalizeToOkx(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("BTC-USDT", "BTC-USDT")]
    [InlineData("ETH-BTC", "ETH-BTC")]
    public void NormalizeToOkx_AlreadyOkxFormat_ShouldReturnAsIs(string input, string expected)
    {
        OkxExecutionService.NormalizeToOkx(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("BTC-USDT", "BTCUSDT")]
    [InlineData("ETH-BTC", "ETHBTC")]
    [InlineData("SOL-USDC", "SOLUSDC")]
    public void NormalizeFromOkx_ShouldRemoveHyphens(string input, string expected)
    {
        OkxExecutionService.NormalizeFromOkx(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeToOkx_UnknownQuote_ShouldReturnAsIs()
    {
        // Single word symbol with no recognizable quote asset
        OkxExecutionService.NormalizeToOkx("XYZ").Should().Be("XYZ");
    }

    [Theory]
    [InlineData("BTCUSDT", "BTC-USDT")]
    [InlineData("BTC-USDT", "BTC-USDT")]
    [InlineData("ETHUSDC", "ETH-USDC")]
    public void NormalizeToOkx_Idempotent_ShouldHandleBothFormats(string input, string expected)
    {
        // Double normalization should be safe
        var first = OkxExecutionService.NormalizeToOkx(input);
        var second = OkxExecutionService.NormalizeToOkx(first);
        first.Should().Be(expected);
        second.Should().Be(expected);
    }
}
