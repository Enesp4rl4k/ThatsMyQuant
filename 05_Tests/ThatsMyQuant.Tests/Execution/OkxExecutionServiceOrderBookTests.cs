using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Execution;

public sealed class OkxExecutionServiceOrderBookTests
{
    [Fact]
    public async Task GetQueuePositionAsync_AfterBooks5Snapshot_MatchesVolumeAheadForBuy()
    {
        var ws = new OkxWebSocketService(NullLogger<OkxWebSocketService>.Instance);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TMQ_EXCHANGE_MODE"] = "OKX" })
            .Build();

        var svc = new OkxExecutionService(
            NullLogger<OkxExecutionService>.Instance,
            new HttpClient(),
            config,
            Substitute.For<ISharedPortfolioStateManager>(),
            apiVault: null,
            operationalTelemetry: null,
            streamingService: ws);

        var bids = new[]
        {
            new OrderBookLevel(99_500m, 2m),
            new OrderBookLevel(99_400m, 5m)
        };
        var asks = new[]
        {
            new OrderBookLevel(99_600m, 1m),
            new OrderBookLevel(99_700m, 3m)
        };

        ws.RaiseOrderBookUpdatedForTests(new OrderBookSnapshot("BTCUSDT", 1, bids, asks));

        var ahead = await svc.GetQueuePositionAsync("BTCUSDT", 99_500m, TradeSide.Buy);
        ahead.Should().Be(1m);
    }
}
