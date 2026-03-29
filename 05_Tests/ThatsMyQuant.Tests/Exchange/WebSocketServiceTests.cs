using FluentAssertions;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Exchange;

/// <summary>
/// Unit tests for WebSocket service symbol normalization and static parsing helpers.
/// Tests cover both Binance and OKX symbol format conversions.
/// WebSocket connection/reconnect tests are excluded (require real network).
/// </summary>
public sealed class WebSocketServiceTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  OKX Symbol Normalization
    // ═══════════════════════════════════════════════════════════════════════════

    // ─── NormalizeToOkxInstId (Binance → OKX) ────────────��────────────────────

    [Theory]
    [InlineData("BTCUSDT", "BTC-USDT")]
    [InlineData("ETHUSDT", "ETH-USDT")]
    [InlineData("SOLUSDT", "SOL-USDT")]
    [InlineData("XRPUSDT", "XRP-USDT")]
    [InlineData("DOGEUSDT", "DOGE-USDT")]
    [InlineData("AVAXUSDT", "AVAX-USDT")]
    public void OkxNormalize_StandardUsdtPairs(string input, string expected)
    {
        OkxWebSocketService.NormalizeToOkxInstId(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("BTCUSDC", "BTC-USDC")]
    [InlineData("ETHUSDC", "ETH-USDC")]
    public void OkxNormalize_UsdcPairs(string input, string expected)
    {
        OkxWebSocketService.NormalizeToOkxInstId(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("ETHBTC", "ETH-BTC")]
    [InlineData("SOLBTC", "SOL-BTC")]
    public void OkxNormalize_BtcQuotePairs(string input, string expected)
    {
        OkxWebSocketService.NormalizeToOkxInstId(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("SOLUSD", "SOL-USD")]
    [InlineData("BTCUSD", "BTC-USD")]
    public void OkxNormalize_UsdPairs(string input, string expected)
    {
        OkxWebSocketService.NormalizeToOkxInstId(input).Should().Be(expected);
    }

    [Fact]
    public void OkxNormalize_AlreadyFormatted_PassesThrough()
    {
        OkxWebSocketService.NormalizeToOkxInstId("BTC-USDT").Should().Be("BTC-USDT");
        OkxWebSocketService.NormalizeToOkxInstId("ETH-USDC").Should().Be("ETH-USDC");
        OkxWebSocketService.NormalizeToOkxInstId("SOL-USD").Should().Be("SOL-USD");
    }

    [Fact]
    public void OkxNormalize_LowerCase_UpperCasesOutput()
    {
        OkxWebSocketService.NormalizeToOkxInstId("btcusdt").Should().Be("BTC-USDT");
        OkxWebSocketService.NormalizeToOkxInstId("ethbtc").Should().Be("ETH-BTC");
        OkxWebSocketService.NormalizeToOkxInstId("btc-usdt").Should().Be("BTC-USDT");
    }

    [Fact]
    public void OkxNormalize_MixedCase_UpperCasesOutput()
    {
        OkxWebSocketService.NormalizeToOkxInstId("BtcUsdt").Should().Be("BTC-USDT");
    }

    [Fact]
    public void OkxNormalize_UnknownQuote_ReturnsUppercasedWithoutDash()
    {
        var result = OkxWebSocketService.NormalizeToOkxInstId("XYZABC");
        result.Should().Be("XYZABC");
        result.Should().NotContain("-");
    }

    [Theory]
    [InlineData("BTCTRY", "BTC-TRY")]
    [InlineData("ETHTRY", "ETH-TRY")]
    public void OkxNormalize_TryPairs(string input, string expected)
    {
        OkxWebSocketService.NormalizeToOkxInstId(input).Should().Be(expected);
    }

    // ─── NormalizeToBinanceSymbol (OKX → Binance) ─────────────────────────────

    [Theory]
    [InlineData("BTC-USDT", "BTCUSDT")]
    [InlineData("ETH-USDT", "ETHUSDT")]
    [InlineData("SOL-USDC", "SOLUSDC")]
    [InlineData("ETH-BTC", "ETHBTC")]
    public void OkxToBinance_RemovesDash(string okxId, string expected)
    {
        OkxWebSocketService.NormalizeToBinanceSymbol(okxId).Should().Be(expected);
    }

    [Fact]
    public void OkxToBinance_LowerCase_UpperCasesOutput()
    {
        OkxWebSocketService.NormalizeToBinanceSymbol("btc-usdt").Should().Be("BTCUSDT");
    }

    [Fact]
    public void OkxToBinance_AlreadyBinanceFormat_PassesThrough()
    {
        OkxWebSocketService.NormalizeToBinanceSymbol("BTCUSDT").Should().Be("BTCUSDT");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  OKX Execution Service — Symbol Normalization (shared logic)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void OkxExecService_And_OkxWsService_ProduceSameInstId()
    {
        // Both services must agree on symbol format for routing consistency
        string[] symbols = { "BTCUSDT", "ETHUSDT", "SOLUSDC", "ETHBTC", "BTCTRY" };

        foreach (var sym in symbols)
        {
            var wsResult = OkxWebSocketService.NormalizeToOkxInstId(sym);
            var execResult = OkxExecutionService.NormalizeToOkxInstId(sym);

            wsResult.Should().Be(execResult,
                $"WS and Execution services must produce the same instId for '{sym}'");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Roundtrip tests
    // ═════════════════════════════════════════════════════════════════════════��═

    [Theory]
    [InlineData("BTCUSDT")]
    [InlineData("ETHUSDT")]
    [InlineData("SOLUSDC")]
    [InlineData("ETHBTC")]
    public void Roundtrip_BinanceToOkxAndBack_PreservesSymbol(string binanceSymbol)
    {
        var okxId = OkxWebSocketService.NormalizeToOkxInstId(binanceSymbol);
        var roundtrip = OkxWebSocketService.NormalizeToBinanceSymbol(okxId);

        roundtrip.Should().Be(binanceSymbol.ToUpperInvariant(),
            "Binance → OKX → Binance roundtrip should preserve the symbol");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Edge cases
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void OkxNormalize_SingleChar_ReturnsUppercased()
    {
        // Edge: very short symbol
        OkxWebSocketService.NormalizeToOkxInstId("X").Should().Be("X");
    }

    [Fact]
    public void OkxNormalize_QuoteCurrencyOnly_DoesNotCrash()
    {
        // "USDT" alone: base would be empty → returns "-USDT"
        // This is an edge case; just verify no crash
        var act = () => OkxWebSocketService.NormalizeToOkxInstId("USDT");
        act.Should().NotThrow();
    }

    [Fact]
    public void OkxToBinance_NoDash_PassesThrough()
    {
        OkxWebSocketService.NormalizeToBinanceSymbol("BTCUSDT").Should().Be("BTCUSDT");
    }

    [Fact]
    public void OkxToBinance_MultipleDashes_RemovesAll()
    {
        // Hypothetical edge case: "BTC-USDT-SWAP" → "BTCUSDTSWAP"
        OkxWebSocketService.NormalizeToBinanceSymbol("BTC-USDT-SWAP").Should().Be("BTCUSDTSWAP");
    }
}
