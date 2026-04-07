using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Infrastructure;

public class BinanceExecutionServiceTests : IDisposable
{
    private const string TestApiKey = "test-api-key";
    private const string TestApiSecret = "test-api-secret-1234567890";

    private readonly ILogger<BinanceExecutionService> _logger = Substitute.For<ILogger<BinanceExecutionService>>();
    private readonly IStreamingMarketDataService _streamingService = Substitute.For<IStreamingMarketDataService>();
    private readonly ISharedPortfolioStateManager _portfolio = Substitute.For<ISharedPortfolioStateManager>();
    private readonly FakeHttpMessageHandler _httpHandler;
    private readonly HttpClient _httpClient;

    public BinanceExecutionServiceTests()
    {
        _httpHandler = new FakeHttpMessageHandler();
        _httpClient = new HttpClient(_httpHandler);
    }

    private BinanceExecutionService CreateService()
        => new(_logger, _httpClient, TestApiKey, TestApiSecret, _streamingService, _portfolio);

    // --- Signature Computation Tests ---

    [Fact]
    public async Task ExecuteOrderAsync_ShouldSendSignedRequest()
    {
        _portfolio.TryReserve(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<decimal>()).Returns(true);
        _httpHandler.ResponseFactory = request =>
        {
            // Verify the request URL contains a signature parameter
            request.RequestUri!.Query.Should().Contain("signature=");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"orderId\": 12345}")
            };
        };

        var signal = CreateSignal();
        var service = CreateService();

        var result = await service.ExecuteOrderAsync(signal);

        result.Should().NotBeNull();
        result.Symbol.Should().Be("BTCUSDT");
    }

    [Fact]
    public async Task ExecuteOrderAsync_SignatureShouldMatchHmacSha256()
    {
        _portfolio.TryReserve(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<decimal>()).Returns(true);
        string? capturedQueryString = null;
        _httpHandler.ResponseFactory = request =>
        {
            var query = request.RequestUri!.Query.TrimStart('?');
            capturedQueryString = query;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"orderId\": 99}")
            };
        };

        var service = CreateService();
        await service.ExecuteOrderAsync(CreateSignal());

        capturedQueryString.Should().NotBeNull();

        // Extract the query part before &signature= and the signature itself
        var signatureIdx = capturedQueryString!.IndexOf("&signature=", StringComparison.Ordinal);
        signatureIdx.Should().BeGreaterThan(0);

        var queryPart = capturedQueryString[..signatureIdx];
        var signaturePart = capturedQueryString[(signatureIdx + "&signature=".Length)..];

        // Recompute expected signature
        var keyBytes = Encoding.UTF8.GetBytes(TestApiSecret);
        var messageBytes = Encoding.UTF8.GetBytes(queryPart);
        var expected = Convert.ToHexString(HMACSHA256.HashData(keyBytes, messageBytes)).ToLowerInvariant();

        signaturePart.Should().Be(expected);
    }

    // --- Insufficient Funds Tests ---

    [Fact]
    public async Task ExecuteOrderAsync_InsufficientFunds_ShouldThrow()
    {
        _portfolio.TryReserve(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<decimal>()).Returns(false);

        var service = CreateService();

        var act = () => service.ExecuteOrderAsync(CreateSignal());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Insufficient localized funds*");
    }

    // --- Exchange Error Handling Tests ---

    [Fact]
    public async Task ExecuteOrderAsync_BinanceReturnsError_ShouldThrowWithDetails()
    {
        _portfolio.TryReserve(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<decimal>()).Returns(true);
        _httpHandler.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"code\":-1013,\"msg\":\"Filter failure: LOT_SIZE\"}")
        };

        var service = CreateService();
        var act = () => service.ExecuteOrderAsync(CreateSignal());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Binance execution failed*LOT_SIZE*");
    }

    // --- Queue Position Tracking Tests ---

    [Fact]
    public async Task GetQueuePositionAsync_NoOrderBook_ShouldReturnZero()
    {
        var service = CreateService();

        var queuePos = await service.GetQueuePositionAsync("BTCUSDT", 100m, TradeSide.Buy);

        queuePos.Should().Be(0m);
    }

    // --- Account Status Tests ---

    [Fact]
    public async Task GetAccountStatusAsync_WithCachedBalance_ShouldReturnCachedValue()
    {
        _portfolio.GetAvailableBalance(VenueKind.Binance, "USDT").Returns(50_000m);

        var service = CreateService();
        var account = await service.GetAccountStatusAsync();

        account.Balance.Should().Be(50_000m);
        account.Provider.Should().Be("Binance");
        account.Id.Should().Be("BINANCE-LIVE-CACHED");
    }

    [Fact]
    public async Task GetAccountStatusAsync_NoCachedBalance_ShouldFetchFromApi()
    {
        _portfolio.GetAvailableBalance(VenueKind.Binance, "USDT").Returns(0m);
        _httpHandler.ResponseFactory = request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("account"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"balances\":[{\"asset\":\"USDT\",\"free\":\"75000\"},{\"asset\":\"BTC\",\"free\":\"0\"}]}")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        var service = CreateService();
        var account = await service.GetAccountStatusAsync();

        account.Balance.Should().Be(75_000m);
        account.Id.Should().Be("BINANCE-LIVE");
        _portfolio.Received(1).SetBalance(VenueKind.Binance, "USDT", 75_000m);
    }

    [Fact]
    public async Task GetAccountStatusAsync_ApiFails_ShouldThrow()
    {
        _portfolio.GetAvailableBalance(VenueKind.Binance, "USDT").Returns(0m);
        _httpHandler.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"code\":-2015,\"msg\":\"Invalid API-key\"}")
        };

        var service = CreateService();
        var act = () => service.GetAccountStatusAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Failed to fetch Binance account status*");
    }

    // --- Request Format Tests ---

    [Fact]
    public async Task ExecuteOrderAsync_ShouldSendCorrectOrderParameters()
    {
        _portfolio.TryReserve(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<decimal>()).Returns(true);
        string? capturedQuery = null;
        _httpHandler.ResponseFactory = request =>
        {
            capturedQuery = request.RequestUri!.Query;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"orderId\": 1}")
            };
        };

        var signal = CreateSignal(side: TradeSide.Sell);
        var service = CreateService();
        await service.ExecuteOrderAsync(signal);

        capturedQuery.Should().Contain("symbol=BTCUSDT");
        capturedQuery.Should().Contain("side=SELL");
        capturedQuery.Should().Contain("type=LIMIT");
        capturedQuery.Should().Contain("timeInForce=GTC");
        capturedQuery.Should().Contain("timestamp=");
    }

    // --- Helpers ---

    private static Signal CreateSignal(
        string symbol = "BTCUSDT",
        decimal price = 100m,
        decimal quantity = 1m,
        TradeSide side = TradeSide.Buy)
        => new()
        {
            Id = Guid.NewGuid(),
            Symbol = symbol,
            Side = side,
            Price = price,
            Quantity = quantity,
            Source = "Test"
        };

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    /// <summary>
    /// Fake HTTP message handler for testing HttpClient calls without hitting real endpoints.
    /// </summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> ResponseFactory { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(ResponseFactory(request));
        }
    }
}
