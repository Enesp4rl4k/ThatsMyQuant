using System;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class OrderFlowImbalanceAnalyzerTests
{
    [Fact]
    public void ComputeOFI_BidPriceIncreases_ReturnsAggressiveBidVolume()
    {
        var prev = new OrderBookSnapshot("BTC", 1000, 
            new[] { new OrderBookLevel(100m, 5m) },
            new[] { new OrderBookLevel(101m, 10m) });

        var curr = new OrderBookSnapshot("BTC", 1001,
            new[] { new OrderBookLevel(100.5m, 8m) }, // Bid price increased, volume is 8
            new[] { new OrderBookLevel(101m, 10m) }); // Ask unchanged

        var ofi = OrderFlowImbalanceAnalyzer.ComputeOFI(prev, curr);

        ofi.Should().Be(8m); // Entire new bid volume is aggressive
    }

    [Fact]
    public void ComputeOFI_AskPriceDecreases_ReturnsNegativeAggressiveAskVolume()
    {
        var prev = new OrderBookSnapshot("BTC", 1000,
            new[] { new OrderBookLevel(100m, 5m) },
            new[] { new OrderBookLevel(101m, 10m) });

        var curr = new OrderBookSnapshot("BTC", 1001,
            new[] { new OrderBookLevel(100m, 5m) }, // Bid unchanged
            new[] { new OrderBookLevel(100.5m, 15m) }); // Ask price decreased, volume is 15

        var ofi = OrderFlowImbalanceAnalyzer.ComputeOFI(prev, curr);

        ofi.Should().Be(-15m); // Net pressure is heavily downward
    }

    [Fact]
    public void ComputeOFI_BidVolumeDropsAtSamePrice_ReturnsNegativeImbalance()
    {
        var prev = new OrderBookSnapshot("BTC", 1000,
            new[] { new OrderBookLevel(100m, 20m) },
            new[] { new OrderBookLevel(101m, 10m) });

        var curr = new OrderBookSnapshot("BTC", 1001,
            new[] { new OrderBookLevel(100m, 15m) }, // Bid price same, volume dropped by 5
            new[] { new OrderBookLevel(101m, 10m) }); // Ask unchanged

        var ofi = OrderFlowImbalanceAnalyzer.ComputeOFI(prev, curr);

        ofi.Should().Be(-5m); // Net pressure is downward due to vanished bids
    }
}
