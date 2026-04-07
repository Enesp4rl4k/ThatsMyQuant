using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Listener.Api.Hubs;
using Xunit;

namespace ThatsMyQuant.Tests.Infrastructure;

/// <summary>
/// Tests the static validation logic inside <see cref="MarketDataHub"/>.
/// Because the Hub methods require full SignalR infrastructure (Groups, Context, Clients),
/// we invoke the private static <c>ValidateAndNormalizeSymbol</c> method via reflection
/// to isolate the pure validation/normalization logic.
/// </summary>
public sealed class MarketDataHubValidationTests
{
    private static readonly MethodInfo ValidateMethod =
        typeof(MarketDataHub).GetMethod(
            "ValidateAndNormalizeSymbol",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "Could not find ValidateAndNormalizeSymbol on MarketDataHub.");

    /// <summary>
    /// Invokes the private static ValidateAndNormalizeSymbol method.
    /// Unwraps <see cref="TargetInvocationException"/> so callers see the real exception.
    /// </summary>
    private static string InvokeValidate(string? symbol)
    {
        try
        {
            return (string)ValidateMethod.Invoke(null, new object?[] { symbol })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    // ── Reject empty / null ──────────────────────────────────────────

    [Fact]
    public void ValidateAndNormalize_NullSymbol_ShouldThrowHubException()
    {
        var act = () => InvokeValidate(null);

        act.Should().Throw<HubException>()
            .WithMessage("*empty*");
    }

    [Fact]
    public void ValidateAndNormalize_EmptyString_ShouldThrowHubException()
    {
        var act = () => InvokeValidate(string.Empty);

        act.Should().Throw<HubException>()
            .WithMessage("*empty*");
    }

    [Fact]
    public void ValidateAndNormalize_WhitespaceOnly_ShouldThrowHubException()
    {
        var act = () => InvokeValidate("   ");

        act.Should().Throw<HubException>()
            .WithMessage("*empty*");
    }

    // ── Reject special characters ────────────────────────────────────

    [Theory]
    [InlineData("BTC@USDT")]
    [InlineData("BTC_USDT")]
    [InlineData("BTC.USDT")]
    [InlineData("BTC USDT")]
    [InlineData("BTC/USDT")]
    [InlineData("BTC$")]
    public void ValidateAndNormalize_SpecialCharacters_ShouldThrowHubException(string symbol)
    {
        var act = () => InvokeValidate(symbol);

        act.Should().Throw<HubException>()
            .WithMessage("*Invalid symbol format*");
    }

    // ── Reject symbols exceeding max length ──────────────────────────

    [Fact]
    public void ValidateAndNormalize_ExceedsMaxLength_ShouldThrowHubException()
    {
        var longSymbol = new string('A', 21); // MaxSymbolLength is 20

        var act = () => InvokeValidate(longSymbol);

        act.Should().Throw<HubException>()
            .WithMessage("*exceeds maximum length*");
    }

    [Fact]
    public void ValidateAndNormalize_ExactMaxLength_ShouldSucceed()
    {
        // 20 chars is within limit and matches the regex
        var symbol = new string('A', 20);

        var result = InvokeValidate(symbol);

        result.Should().Be(symbol);
    }

    // ── Reject single-character symbols (regex requires min 2) ───────

    [Fact]
    public void ValidateAndNormalize_SingleCharacter_ShouldThrowHubException()
    {
        var act = () => InvokeValidate("A");

        act.Should().Throw<HubException>()
            .WithMessage("*Invalid symbol format*");
    }

    // ── Normalize lowercase to uppercase ─────────────────────────────

    [Fact]
    public void ValidateAndNormalize_LowercaseSymbol_ShouldReturnUppercase()
    {
        var result = InvokeValidate("btcusdt");

        result.Should().Be("BTCUSDT");
    }

    [Fact]
    public void ValidateAndNormalize_MixedCaseSymbol_ShouldReturnUppercase()
    {
        var result = InvokeValidate("Btc-Usdt");

        result.Should().Be("BTC-USDT");
    }

    [Fact]
    public void ValidateAndNormalize_LeadingTrailingWhitespace_ShouldTrimAndNormalize()
    {
        var result = InvokeValidate("  ethusdt  ");

        result.Should().Be("ETHUSDT");
    }

    // ── Valid symbols should pass ────────────────────────────────────

    [Theory]
    [InlineData("BTCUSDT", "BTCUSDT")]
    [InlineData("BTC-USDT", "BTC-USDT")]
    [InlineData("ETH-BTC", "ETH-BTC")]
    [InlineData("XRP123", "XRP123")]
    public void ValidateAndNormalize_ValidSymbol_ShouldReturnNormalized(
        string input, string expected)
    {
        var result = InvokeValidate(input);

        result.Should().Be(expected);
    }

    // ── Clamp verification note ──────────────────────────────────────
    // UpdateSystemVitals uses Math.Clamp(value, 0, 100) inline before
    // broadcasting via Clients.All.SendAsync. Testing that path requires
    // a full SignalR Hub test harness (mocked IHubCallerClients etc.).
    // Math.Clamp is a framework primitive, so a dedicated unit test for
    // clamping offers low value; the integration path is better covered
    // by end-to-end or integration tests.
}
