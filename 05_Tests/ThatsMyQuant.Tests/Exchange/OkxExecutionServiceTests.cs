using FluentAssertions;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Exchange;

/// <summary>
/// Unit tests for OkxExecutionService — covers signing logic and symbol normalisation.
/// HTTP calls are NOT tested here (those belong in integration tests).
/// </summary>
public sealed class OkxExecutionServiceTests
{
    // ─── ComputeSignature ────────────────────────────────────────────────────

    [Fact]
    public void ComputeSignature_IsDeterministic_ForSameInputs()
    {
        const string ts = "2026-03-27T12:00:00.000Z";
        const string secret = "test-secret-key";

        var sig1 = OkxExecutionService.ComputeSignature(ts, "POST", "/api/v5/trade/order", "{}", secret);
        var sig2 = OkxExecutionService.ComputeSignature(ts, "POST", "/api/v5/trade/order", "{}", secret);

        sig1.Should().Be(sig2, "same inputs must always produce the same signature");
    }

    [Fact]
    public void ComputeSignature_IsBase64Encoded()
    {
        const string ts = "2026-03-27T12:00:00.000Z";
        const string secret = "my-secret";

        var sig = OkxExecutionService.ComputeSignature(ts, "POST", "/api/v5/trade/order", "", secret);

        // Base64 characters: A-Z a-z 0-9 + / =
        sig.Should().MatchRegex(@"^[A-Za-z0-9+/]+=*$",
            "OKX requires HMAC-SHA256 in Base64 (not hex like Binance)");
    }

    [Fact]
    public void ComputeSignature_DiffersForDifferentTimestamps()
    {
        const string secret = "s3cr3t";
        var sig1 = OkxExecutionService.ComputeSignature("2026-01-01T00:00:00.000Z", "GET", "/api/v5/account/balance", "", secret);
        var sig2 = OkxExecutionService.ComputeSignature("2026-01-01T00:00:01.000Z", "GET", "/api/v5/account/balance", "", secret);

        sig1.Should().NotBe(sig2, "different timestamps must produce different signatures");
    }

    [Fact]
    public void ComputeSignature_DiffersForDifferentMethods()
    {
        const string ts = "2026-03-27T12:00:00.000Z";
        const string secret = "s3cr3t";

        var get = OkxExecutionService.ComputeSignature(ts, "GET", "/api/v5/account/balance", "", secret);
        var post = OkxExecutionService.ComputeSignature(ts, "POST", "/api/v5/account/balance", "", secret);

        get.Should().NotBe(post, "GET and POST must sign differently");
    }

    [Fact]
    public void ComputeSignature_DiffersForDifferentBodies()
    {
        const string ts = "2026-03-27T12:00:00.000Z";
        const string secret = "s3cr3t";

        var empty = OkxExecutionService.ComputeSignature(ts, "POST", "/api/v5/trade/order", "", secret);
        var withBody = OkxExecutionService.ComputeSignature(ts, "POST", "/api/v5/trade/order", "{\"instId\":\"BTC-USDT\"}", secret);

        empty.Should().NotBe(withBody, "body content must affect the signature");
    }

    [Fact]
    public void ComputeSignature_DiffersForDifferentSecrets()
    {
        const string ts = "2026-03-27T12:00:00.000Z";
        var sig1 = OkxExecutionService.ComputeSignature(ts, "GET", "/api/v5/account/balance", "", "secret-A");
        var sig2 = OkxExecutionService.ComputeSignature(ts, "GET", "/api/v5/account/balance", "", "secret-B");

        sig1.Should().NotBe(sig2, "different secrets must produce different signatures");
    }

    // ─── NormalizeToOkxInstId ─────────────────────────────────────────────────

    [Theory]
    [InlineData("BTCUSDT",  "BTC-USDT")]
    [InlineData("ETHUSDT",  "ETH-USDT")]
    [InlineData("SOLUSDT",  "SOL-USDT")]
    [InlineData("BTCUSDC",  "BTC-USDC")]
    [InlineData("ETHBTC",   "ETH-BTC")]
    [InlineData("SOLUSD",   "SOL-USD")]
    public void NormalizeToOkxInstId_ConvertsFromBinanceStyle(string input, string expected)
    {
        OkxExecutionService.NormalizeToOkxInstId(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeToOkxInstId_PassthroughAlreadyFormatted()
    {
        // OKX-formatted symbols must not be double-dashed
        OkxExecutionService.NormalizeToOkxInstId("BTC-USDT").Should().Be("BTC-USDT");
        OkxExecutionService.NormalizeToOkxInstId("ETH-USDT").Should().Be("ETH-USDT");
    }

    [Fact]
    public void NormalizeToOkxInstId_UpperCasesOutput()
    {
        OkxExecutionService.NormalizeToOkxInstId("btcusdt").Should().Be("BTC-USDT");
        OkxExecutionService.NormalizeToOkxInstId("ethusdt").Should().Be("ETH-USDT");
    }

    [Fact]
    public void NormalizeToOkxInstId_UnknownSymbol_UpperCasesWithoutDash()
    {
        // Unknown quote currency — returns uppercased as-is without a dash
        var result = OkxExecutionService.NormalizeToOkxInstId("XYZABC");
        result.Should().Be("XYZABC");
        result.Should().NotContain("-");
    }
}
