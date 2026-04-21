using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public sealed class OrderBookExecutionAdvisorTests
{
    [Fact]
    public void WideSpread_Blocks_WhenConfigured()
    {
        var signal = new Signal { Symbol = "BTCUSDT", Side = TradeSide.Buy, Quantity = 1m };
        var bids = new[] { new OrderBookLevel(100m, 1m) };
        var asks = new[] { new OrderBookLevel(102m, 1m) };
        var book = new OrderBookSnapshot("BTCUSDT", 1, bids, asks);

        var opts = new OrderBookExecutionOptions { BlockOnWideSpread = true, MaxSpreadPercent = 0.5m };

        var ok = OrderBookExecutionAdvisor.TryApply(signal, book, opts, isHftMode: false, NullLogger.Instance, out var reason);
        ok.Should().BeFalse();
        reason.Should().Contain("spread");
    }

    [Fact]
    public void WeakBidSide_ReducesBuyQuantity()
    {
        var signal = new Signal { Symbol = "BTCUSDT", Side = TradeSide.Buy, Quantity = 10m };
        var bids = new[] { new OrderBookLevel(100m, 0.1m) };
        var asks = new[] { new OrderBookLevel(100.02m, 10m) };
        var book = new OrderBookSnapshot("BTCUSDT", 1, bids, asks);

        var opts = new OrderBookExecutionOptions
        {
            BlockOnWideSpread = false,
            ReduceQuantityOnWeakSide = true,
            MinTopImbalanceForBuy = 0.45m,
            QuantityReductionFactor = 0.5m
        };

        var ok = OrderBookExecutionAdvisor.TryApply(signal, book, opts, isHftMode: false, NullLogger.Instance, out _);
        ok.Should().BeTrue();
        signal.Quantity.Should().Be(5m);
    }

    [Fact]
    public void HftMode_IsBypassed_WhenEnableForHftIsFalse()
    {
        var signal = new Signal { Symbol = "BTCUSDT", Side = TradeSide.Buy, Quantity = 10m, Confidence = 60 };
        var bids = new[] { new OrderBookLevel(100m, 0.1m) };
        var asks = new[] { new OrderBookLevel(102m, 10m) };
        var book = new OrderBookSnapshot("BTCUSDT", 1, bids, asks);

        var opts = new OrderBookExecutionOptions
        {
            EnableForHft = false,
            BlockOnWideSpread = true,
            MaxSpreadPercent = 0.20m,
            UseForAlphaModulation = true
        };

        var ok = OrderBookExecutionAdvisor.TryApply(signal, book, opts, isHftMode: true, NullLogger.Instance, out var reason);
        ok.Should().BeTrue();
        reason.Should().BeNull();
        signal.Quantity.Should().Be(10m);
        signal.Confidence.Should().Be(60);
    }
}
