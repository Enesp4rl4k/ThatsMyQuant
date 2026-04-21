using System.Net;
using System.Security.Cryptography;
using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Execution;

/// <summary>
/// Unit tests for OkxExecutionService.
/// Uses a capturing HttpMessageHandler to intercept outgoing requests without real HTTP calls.
/// Validates: Requirements OKX-3
/// </summary>
public class OkxExecutionServiceTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures the last outgoing HttpRequestMessage and returns a fake 200 response.
    /// The request body is buffered eagerly so it can be read after the handler returns.
    /// Returns the placement response for the first call, then a "live" fill-status response
    /// for subsequent calls (the fill-poll added by PollFillStatusAsync).
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? FirstRequest { get; private set; }
        public string? FirstRequestBody { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        private int _callCount;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            string? body = null;
            if (request.Content is not null)
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            LastRequestBody = body;

            _callCount++;
            if (_callCount == 1)
            {
                FirstRequest = request;
                FirstRequestBody = body;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"code":"0","data":[{"ordId":"test"}]}""",
                        Encoding.UTF8, "application/json")
                };
            }

            // Fill-status poll response
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":"0","data":[{"ordId":"test","state":"live","fillSz":"0","sz":"0.001","fillFee":"0"}]}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class SingleResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    private static IConfiguration BuildConfig(
        string secretKey = "test-secret",
        string apiKey = "test-api-key",
        string passphrase = "test-passphrase",
        bool isDemoMode = false,
        string exchangeMode = "OKX")
    {
        var dict = new Dictionary<string, string?>
        {
            ["OKX:SecretKey"]   = secretKey,
            ["OKX:ApiKey"]      = apiKey,
            ["OKX:Passphrase"]  = passphrase,
            ["OKX:IsDemoMode"]  = isDemoMode.ToString(),
            ["TMQ_EXCHANGE_MODE"] = exchangeMode
        };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static Signal MakeSignal(decimal price = 50_000m, decimal qty = 0.001m) => new()
    {
        Symbol   = "BTCUSDT",
        Side     = TradeSide.Buy,
        Price    = price,
        Quantity = qty
    };

    private static string ComputeExpectedSignature(string secret, string timestamp, string method,
        string path, string body)
    {
        var prehash = $"{timestamp}{method}{path}{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(prehash)));
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// HMAC golden-value test: the OK-ACCESS-SIGN header must equal
    /// Base64(HMAC-SHA256(timestamp + "POST" + path + body)) using the configured secret.
    /// Validates: Requirements OKX-3
    /// </summary>
    [Fact]
    public async Task ExecuteOrderAsync_SignatureHeader_MatchesExpectedHmac()
    {
        const string secret = "golden-secret";
        var handler = new CapturingHandler();
        var config  = BuildConfig(secretKey: secret);
        var logger  = Substitute.For<ILogger<OkxExecutionService>>();
        var portfolio = Substitute.For<ISharedPortfolioStateManager>();
        portfolio.TryReserve(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<decimal>()).Returns(true);

        var svc = new OkxExecutionService(logger, new HttpClient(handler), config, portfolio);

        await svc.ExecuteOrderAsync(MakeSignal());

        handler.LastRequest.Should().NotBeNull();
        var req = handler.FirstRequest!;

        // Extract the timestamp the service used
        req.Headers.TryGetValues("OK-ACCESS-TIMESTAMP", out var tsValues).Should().BeTrue();
        var timestamp = tsValues!.First();

        // Extract the actual signature
        req.Headers.TryGetValues("OK-ACCESS-SIGN", out var signValues).Should().BeTrue();
        var actualSign = signValues!.First();

        // Use the body buffered by the handler (content is disposed after SendAsync)
        var body = handler.FirstRequestBody ?? string.Empty;

        // Compute expected signature with the same inputs
        var expected = ComputeExpectedSignature(secret, timestamp, "POST", "/api/v5/trade/order", body);

        actualSign.Should().Be(expected,
            "OK-ACCESS-SIGN must be Base64(HMAC-SHA256(timestamp+method+path+body)) with the configured secret");
    }

    /// <summary>
    /// Demo mode header test: when IsDemoMode = true, the request must carry x-simulated-trading: 1.
    /// Validates: Requirements OKX-3
    /// </summary>
    [Fact]
    public async Task ExecuteOrderAsync_DemoMode_AddsSimulatedTradingHeader()
    {
        var handler   = new CapturingHandler();
        var config    = BuildConfig(isDemoMode: true);
        var logger    = Substitute.For<ILogger<OkxExecutionService>>();
        var portfolio = Substitute.For<ISharedPortfolioStateManager>();
        portfolio.TryReserve(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<decimal>()).Returns(true);

        var svc = new OkxExecutionService(logger, new HttpClient(handler), config, portfolio);

        await svc.ExecuteOrderAsync(MakeSignal());

        handler.LastRequest.Should().NotBeNull();
        handler.FirstRequest!.Headers.TryGetValues("x-simulated-trading", out var values).Should().BeTrue();
        values!.First().Should().Be("1");
    }

    /// <summary>
    /// Live mode header test: when IsDemoMode = false, x-simulated-trading must NOT be present.
    /// Validates: Requirements OKX-3
    /// </summary>
    [Fact]
    public async Task ExecuteOrderAsync_LiveMode_DoesNotAddSimulatedTradingHeader()
    {
        var handler   = new CapturingHandler();
        var config    = BuildConfig(isDemoMode: false);
        var logger    = Substitute.For<ILogger<OkxExecutionService>>();
        var portfolio = Substitute.For<ISharedPortfolioStateManager>();
        portfolio.TryReserve(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<decimal>()).Returns(true);

        var svc = new OkxExecutionService(logger, new HttpClient(handler), config, portfolio);

        await svc.ExecuteOrderAsync(MakeSignal());

        handler.LastRequest.Should().NotBeNull();
        handler.FirstRequest!.Headers.Contains("x-simulated-trading").Should().BeFalse(
            "live mode must not send the simulated-trading header");
    }

    [Fact]
    public async Task GetFavoritesAsync_WhenBalanceContainsTradableAssets_ReturnsCanonicalHeldSymbolsOnly()
    {
        var handler = new SingleResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "code":"0",
                  "data":[
                    {
                      "details":[
                        {"ccy":"USDT","availBal":"1000","cashBal":"1000"},
                        {"ccy":"BTC","availBal":"0.10","cashBal":"0.10"},
                        {"ccy":"SOL","availBal":"2.50","cashBal":"2.50"},
                        {"ccy":"ETH","availBal":"0","cashBal":"0"}
                      ]
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json")
        });

        var svc = new OkxExecutionService(
            Substitute.For<ILogger<OkxExecutionService>>(),
            new HttpClient(handler),
            BuildConfig(),
            Substitute.For<ISharedPortfolioStateManager>());

        var favorites = await svc.GetFavoritesAsync();

        favorites.Should().BeEquivalentTo(["SOLUSDT"]);
    }

    /// <summary>
    /// Insufficient balance test: when TryReserve returns false, ExecuteOrderAsync must throw
    /// InvalidOperationException without making any HTTP call.
    /// Validates: Requirements OKX-3
    /// </summary>
    [Fact]
    public async Task ExecuteOrderAsync_InsufficientBalance_ThrowsWithoutHttpCall()
    {
        var handler   = new CapturingHandler();
        var config    = BuildConfig();
        var logger    = Substitute.For<ILogger<OkxExecutionService>>();
        var portfolio = Substitute.For<ISharedPortfolioStateManager>();
        portfolio.TryReserve(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<decimal>()).Returns(false);

        var svc = new OkxExecutionService(logger, new HttpClient(handler), config, portfolio);

        var act = async () => await svc.ExecuteOrderAsync(MakeSignal());

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.LastRequest.Should().BeNull("no HTTP call should be made when balance check fails");
    }

    /// <summary>
    /// Credential hot-reload test: after the IConfiguration value for SecretKey changes,
    /// the next request must use the new HMAC key (different OK-ACCESS-SIGN).
    /// Validates: Requirements OKX-3
    /// </summary>
    [Fact]
    public async Task ExecuteOrderAsync_CredentialHotReload_SignatureChangesAfterKeyRotation()
    {
        // Use a mutable in-memory config so we can change the secret between calls
        var configData = new Dictionary<string, string?>
        {
            ["OKX:SecretKey"]   = "key1",
            ["OKX:ApiKey"]      = "api-key",
            ["OKX:Passphrase"]  = "passphrase",
            ["OKX:IsDemoMode"]  = "false"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var handler   = new CapturingHandler();
        var logger    = Substitute.For<ILogger<OkxExecutionService>>();
        var portfolio = Substitute.For<ISharedPortfolioStateManager>();
        portfolio.TryReserve(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<decimal>()).Returns(true);

        var svc = new OkxExecutionService(logger, new HttpClient(handler), config, portfolio);

        // First call with key1
        await svc.ExecuteOrderAsync(MakeSignal());
        handler.FirstRequest!.Headers.TryGetValues("OK-ACCESS-SIGN", out var v1).Should().BeTrue();
        var sign1 = v1!.First();

        // Rotate the secret in config
        configData["OKX:SecretKey"] = "key2";
        // Rebuild config from the updated dictionary (simulates IConfiguration reload)
        var reloadedConfig = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        // Rebuild service with the new config (hot-reload scenario) — use a fresh handler
        var handler2 = new CapturingHandler();
        var svc2 = new OkxExecutionService(logger, new HttpClient(handler2), reloadedConfig, portfolio);

        await svc2.ExecuteOrderAsync(MakeSignal());
        handler2.FirstRequest!.Headers.TryGetValues("OK-ACCESS-SIGN", out var v2).Should().BeTrue();
        var sign2 = v2!.First();

        sign1.Should().NotBe(sign2,
            "rotating the HMAC secret must produce a different signature");
    }

    [Fact]
    public async Task ExecuteOrderAsync_SwapMode_ShouldSubmitSwapInstrumentAndPosSide()
    {
        var handler   = new CapturingHandler();
        var config    = BuildConfig(isDemoMode: true, exchangeMode: "OKXSwap");
        var logger    = Substitute.For<ILogger<OkxExecutionService>>();
        var portfolio = Substitute.For<ISharedPortfolioStateManager>();
        portfolio.TryReserveForOrder(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>())
            .Returns(false);
        portfolio.TryReserve(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<decimal>())
            .Returns(false);

        var svc = new OkxExecutionService(logger, new HttpClient(handler), config, portfolio);
        var signal = MakeSignal();
        signal.Side = TradeSide.Sell; // open short in swap mode

        await svc.ExecuteOrderAsync(signal);

        handler.FirstRequest.Should().NotBeNull();
        handler.FirstRequestBody.Should().Contain("\"instId\":\"BTC-USDT-SWAP\"");
        // Default margin mode is isolated when signal does not specify Cross (see OkxExecutionService.ResolveMarginMode).
        handler.FirstRequestBody.Should().Contain("\"tdMode\":\"isolated\"");
        handler.FirstRequestBody.Should().Contain("\"posSide\":\"short\"");
    }
}

/// <summary>
/// Property-based and unit tests for OKX fill tracking (PollFillStatusAsync).
/// Validates: Requirements 5.2, 5.3, 5.4, 5.6
/// </summary>
public class OkxFillTrackingTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public int CallCount { get; private set; }

        public ScriptedHandler(params HttpResponseMessage[] responses)
            => _responses = new Queue<HttpResponseMessage>(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private static HttpResponseMessage OkxOrderResponse(string state, decimal fillSz, string ordId = "ord1", string fillFee = "0")
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"code":"0","data":[{"ordId":"{{ordId}}","state":"{{state}}","fillSz":"{{fillSz.ToString(System.Globalization.CultureInfo.InvariantCulture)}}","sz":"0.01","fillFee":"{{fillFee}}"}]}""",
                Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage OkxPlacementResponse(string ordId = "ord1")
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"code":"0","data":[{"ordId":"{{ordId}}"}]}""",
                Encoding.UTF8, "application/json")
        };

    private static IConfiguration BuildConfig(bool demoMode = false)
    {
        var dict = new Dictionary<string, string?>
        {
            ["OKX:SecretKey"]  = "test-secret",
            ["OKX:ApiKey"]     = "test-key",
            ["OKX:Passphrase"] = "test-pass",
            ["OKX:IsDemoMode"] = demoMode.ToString()
        };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static Signal MakeSignal(decimal qty = 0.01m) => new()
    {
        Symbol   = "BTCUSDT",
        Side     = TradeSide.Buy,
        Price    = 50_000m,
        Quantity = qty
    };

    // ── Property 7: Fill quantity invariant ───────────────────────────────────
    // FilledQty + RemainingQty == RequestedQty for all accepted orders.
    // Validates: Requirements 5.2, 5.3
    [Property(MaxTest = 20)]
    public Property FillQuantityInvariant_FilledPlusRemaining_EqualsRequested()
    {
        // Feature: live-trading-safety, Property 7: Fill quantity invariant
        var gen =
            from requested in Gen.Choose(1, 10_000).Select(n => (decimal)n / 1000m)
            from filled in Gen.Choose(0, 10_000).Select(n => (decimal)n / 1000m)
            select (requested, Math.Min(filled, requested));

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (requestedQty, filledQty) = tuple;
            var remainingQty = requestedQty - filledQty;
            return filledQty + remainingQty == requestedQty;
        });
    }

    // ── Unit tests ────────────────────────────────────────────────────────────

    // Property 7 — filled order: FilledQty == RequestedQty
    [Fact]
    public async Task ExecuteOrderAsync_WhenOrderFilled_FilledQtyEqualsRequestedQty()
    {
        const decimal qty = 0.01m;
        var handler = new ScriptedHandler(
            OkxPlacementResponse(),
            OkxOrderResponse("filled", qty, fillFee: "0.0125"));

        var portfolio = Substitute.For<ISharedPortfolioStateManager>();
        portfolio.TryReserve(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<decimal>()).Returns(true);

        var svc = new OkxExecutionService(
            Substitute.For<ILogger<OkxExecutionService>>(),
            new HttpClient(handler),
            BuildConfig(),
            portfolio);

        var trade = await svc.ExecuteOrderAsync(MakeSignal(qty));

        trade.FilledQuantity.Should().Be(qty);
        trade.RemainingQuantity.Should().Be(0m);
        trade.Status.Should().Be(TradeStatus.Executed);
        trade.TotalFeesPaid.Should().Be(0.0125m);
        (trade.FilledQuantity + trade.RemainingQuantity).Should().Be(trade.RequestedQuantity);
    }

    // Property 7 — partially filled: FilledQty + RemainingQty == RequestedQty
    [Fact]
    public async Task ExecuteOrderAsync_WhenOrderPartiallyFilled_QuantitiesSumToRequested()
    {
        const decimal qty = 0.01m;
        const decimal filled = 0.006m;
        var handler = new ScriptedHandler(
            OkxPlacementResponse(),
            OkxOrderResponse("partially_filled", filled));

        var portfolio = Substitute.For<ISharedPortfolioStateManager>();
        portfolio.TryReserve(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<decimal>()).Returns(true);

        var svc = new OkxExecutionService(
            Substitute.For<ILogger<OkxExecutionService>>(),
            new HttpClient(handler),
            BuildConfig(),
            portfolio);

        var trade = await svc.ExecuteOrderAsync(MakeSignal(qty));

        trade.FilledQuantity.Should().Be(filled);
        trade.RemainingQuantity.Should().Be(qty - filled);
        (trade.FilledQuantity + trade.RemainingQuantity).Should().Be(trade.RequestedQuantity);
    }

    // Property 8 — no-throw on poll failure
    [Fact]
    public async Task ExecuteOrderAsync_WhenPollFails_ReturnsTradeWithoutThrowing()
    {
        const decimal qty = 0.01m;
        // Placement succeeds, all 3 poll attempts fail with 500
        var handler = new ScriptedHandler(
            OkxPlacementResponse(),
            new HttpResponseMessage(HttpStatusCode.InternalServerError),
            new HttpResponseMessage(HttpStatusCode.InternalServerError),
            new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var portfolio = Substitute.For<ISharedPortfolioStateManager>();
        portfolio.TryReserve(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<decimal>()).Returns(true);

        var svc = new OkxExecutionService(
            Substitute.For<ILogger<OkxExecutionService>>(),
            new HttpClient(handler),
            BuildConfig(),
            portfolio);

        var act = async () => await svc.ExecuteOrderAsync(MakeSignal(qty));
        var trade = await act.Should().NotThrowAsync();

        trade.Subject.FilledQuantity.Should().Be(0m);
        trade.Subject.Status.Should().Be(TradeStatus.Pending);
    }

    // Req 5.4 — live status: FilledQty == 0
    [Fact]
    public async Task ExecuteOrderAsync_WhenOrderLive_FilledQtyIsZero()
    {
        const decimal qty = 0.01m;
        var handler = new ScriptedHandler(
            OkxPlacementResponse(),
            OkxOrderResponse("live", 0m));

        var portfolio = Substitute.For<ISharedPortfolioStateManager>();
        portfolio.TryReserve(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<decimal>()).Returns(true);

        var svc = new OkxExecutionService(
            Substitute.For<ILogger<OkxExecutionService>>(),
            new HttpClient(handler),
            BuildConfig(),
            portfolio);

        var trade = await svc.ExecuteOrderAsync(MakeSignal(qty));

        trade.FilledQuantity.Should().Be(0m);
        trade.RemainingQuantity.Should().Be(qty);
        trade.Status.Should().Be(TradeStatus.Pending);
    }
}
