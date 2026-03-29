using FluentAssertions;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Infrastructure;

public class MarketReplayServiceTests
{
    [Fact]
    public async Task ConnectAsync_ShouldParseCompactDepthSnapshots()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllLinesAsync(tempFile,
        [
            "Type,Symbol,Price,Qty,Timestamp,IsMaker,DepthData",
            "DEPTH,BTCUSDT,65000.00,1.50,1710000000000,false,bids=64999.50:3.0|64999.00:2.0;asks=65000.50:4.0|65001.00:1.5"
        ]);

        try
        {
            var replay = new MarketReplayService(tempFile);
            var received = new TaskCompletionSource<OrderBookSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            replay.OnOrderBookUpdated += snapshot => received.TrySetResult(snapshot);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await replay.ConnectAsync(["BTCUSDT"], cts.Token);

            var snapshot = await received.Task.WaitAsync(cts.Token);

            snapshot.Symbol.Should().Be("BTCUSDT");
            snapshot.Bids.Should().HaveCount(2);
            snapshot.Asks.Should().HaveCount(2);
            snapshot.Bids[0].Price.Should().Be(64999.50m);
            snapshot.Bids[0].Quantity.Should().Be(3.0m);
            snapshot.Asks[0].Price.Should().Be(65000.50m);
            snapshot.Asks[0].Quantity.Should().Be(4.0m);

            await replay.DisconnectAsync();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
