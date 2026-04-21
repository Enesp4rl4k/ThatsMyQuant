using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ThatsMyQuant.Application.Security;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Execution;

public class BinanceExecutionServiceTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"symbol":"BTCUSDT","status":"FILLED","origQty":"0.25000000","executedQty":"0.25000000","price":"64000.00","fills":[{"price":"64010.00","qty":"0.25000000","commission":"4.00062500","commissionAsset":"USDT"}]}
                    """,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    [Fact]
    public async Task ExecuteOrderAsync_UsesVaultBackedCredentialsAndParsesFilledTrade()
    {
        using var keyVault = new ApiVault();
        using var secretVault = new ApiVault();
        keyVault.SecureInboundKey("test-key");
        secretVault.SecureInboundKey("test-secret");

        var handler = new CapturingHandler();
        var portfolio = Substitute.For<ISharedPortfolioStateManager>();
        portfolio.TryReserve(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<decimal>()).Returns(true);

        var sut = new BinanceExecutionService(
            NullLogger<BinanceExecutionService>.Instance,
            new HttpClient(handler),
            keyVault,
            secretVault,
            Substitute.For<IStreamingMarketDataService>(),
            portfolio);

        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTCUSDT",
            Side = TradeSide.Buy,
            Price = 64000m,
            Quantity = 0.25m
        };

        var trade = await sut.ExecuteOrderAsync(signal);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.GetValues("X-MBX-APIKEY").Single().Should().Be("test-key");
        handler.LastRequest.RequestUri!.Query.Should().Contain("newClientOrderId=");
        trade.Status.Should().Be(TradeStatus.Executed);
        trade.Quantity.Should().Be(0.25m);
        trade.FilledQuantity.Should().Be(0.25m);
        trade.RemainingQuantity.Should().Be(0m);
        trade.MarkPrice.Should().Be(64010m);
        trade.TotalFeesPaid.Should().Be(4.000625m);
    }

    [Fact]
    public async Task ExecuteOrderAsync_FuturesMode_ShouldUseFuturesEndpointAndAllowShort()
    {
        using var keyVault = new ApiVault();
        using var secretVault = new ApiVault();
        keyVault.SecureInboundKey("test-key");
        secretVault.SecureInboundKey("test-secret");

        var handler = new CapturingHandler();
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://fapi.binance.com")
        };

        var portfolio = Substitute.For<ISharedPortfolioStateManager>();
        // Futures short should not require spot-style reserve.
        portfolio.TryReserveForOrder(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>())
            .Returns(false);
        portfolio.TryReserve(Arg.Any<VenueKind>(), Arg.Any<string>(), Arg.Any<decimal>())
            .Returns(false);

        var sut = new BinanceExecutionService(
            NullLogger<BinanceExecutionService>.Instance,
            httpClient,
            keyVault,
            secretVault,
            Substitute.For<IStreamingMarketDataService>(),
            portfolio);

        var signal = new Signal
        {
            Id = Guid.NewGuid(),
            Symbol = "BTCUSDT",
            Side = TradeSide.Sell,
            Price = 64000m,
            Quantity = 0.25m
        };

        var trade = await sut.ExecuteOrderAsync(signal);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/fapi/v1/order");
        handler.LastRequest.RequestUri.Query.Should().Contain("side=SELL");
        handler.LastRequest.RequestUri.Query.Should().Contain("reduceOnly=false");
        trade.ExecutionMode.Should().Be("REAL-FUTURES");
    }
}
