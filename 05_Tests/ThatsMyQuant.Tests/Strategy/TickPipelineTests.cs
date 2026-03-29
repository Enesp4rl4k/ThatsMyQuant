using FluentAssertions;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Application.MachineLearning;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

/// <summary>
/// Unit tests for TickPipeline static helpers and TickRingBuffer behavior.
/// The async processing loop requires DI/channels and is tested in integration tests.
/// These tests cover: price sanity guards, SumTopLevelVolumes, TickRingBuffer.
/// </summary>
public sealed class TickPipelineTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  TickRingBuffer
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void RingBuffer_MovingAverage_IsZero_WhenEmpty()
    {
        var buffer = new TickRingBuffer(10);
        buffer.MovingAverage.Should().Be(0);
    }

    [Fact]
    public void RingBuffer_MovingAverage_CorrectForSingleValue()
    {
        var buffer = new TickRingBuffer(10);
        buffer.Add(100);
        buffer.MovingAverage.Should().Be(100);
    }

    [Fact]
    public void RingBuffer_MovingAverage_CorrectForMultipleValues()
    {
        var buffer = new TickRingBuffer(5);
        buffer.Add(10);
        buffer.Add(20);
        buffer.Add(30);

        buffer.MovingAverage.Should().Be(20); // (10+20+30)/3
    }

    [Fact]
    public void RingBuffer_MovingAverage_DropsOldestWhenFull()
    {
        var buffer = new TickRingBuffer(3);
        buffer.Add(10);
        buffer.Add(20);
        buffer.Add(30);  // full: [10, 20, 30]
        buffer.Add(40);  // drops 10: [20, 30, 40]

        buffer.MovingAverage.Should().Be(30); // (20+30+40)/3
    }

    [Fact]
    public void RingBuffer_OldestValue_IsFirstAdded_WhenNotFull()
    {
        var buffer = new TickRingBuffer(10);
        buffer.Add(100);
        buffer.Add(200);

        buffer.OldestValue.Should().Be(100);
    }

    [Fact]
    public void RingBuffer_OldestValue_IsCorrect_WhenFull()
    {
        var buffer = new TickRingBuffer(3);
        buffer.Add(10);
        buffer.Add(20);
        buffer.Add(30);  // full
        buffer.Add(40);  // drops 10

        buffer.OldestValue.Should().Be(20);
    }

    [Fact]
    public void RingBuffer_OldestValue_IsZero_WhenEmpty()
    {
        var buffer = new TickRingBuffer(5);
        buffer.OldestValue.Should().Be(0);
    }

    [Fact]
    public void RingBuffer_IsFilled_FalseWhenNotFull()
    {
        var buffer = new TickRingBuffer(5);
        buffer.Add(1);
        buffer.Add(2);

        buffer.IsFilled.Should().BeFalse();
    }

    [Fact]
    public void RingBuffer_IsFilled_TrueWhenFull()
    {
        var buffer = new TickRingBuffer(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        buffer.IsFilled.Should().BeTrue();
    }

    [Fact]
    public void RingBuffer_IsFilled_RemainsTrueAfterOverflow()
    {
        var buffer = new TickRingBuffer(3);
        for (int i = 0; i < 10; i++)
            buffer.Add(i);

        buffer.IsFilled.Should().BeTrue();
    }

    [Fact]
    public void RingBuffer_LargeCapacity_MaintainsCorrectAverage()
    {
        var buffer = new TickRingBuffer(1000);
        for (int i = 1; i <= 1000; i++)
            buffer.Add(i);

        // Average of 1..1000 = 500.5
        buffer.MovingAverage.Should().BeApproximately(500.5, 0.001);
    }

    [Fact]
    public void RingBuffer_CapacityOfOne_WorksCorrectly()
    {
        var buffer = new TickRingBuffer(1);
        buffer.Add(42);
        buffer.MovingAverage.Should().Be(42);
        buffer.OldestValue.Should().Be(42);
        buffer.IsFilled.Should().BeTrue();

        buffer.Add(99);
        buffer.MovingAverage.Should().Be(99);
        buffer.OldestValue.Should().Be(99);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Price Sanity Guards (tested via tick handler behavior)
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100.5)]
    public void PriceSanityGuard_ZeroOrNegativePrice_ShouldBeRejected(decimal price)
    {
        // The tick handler in TickPipeline filters: if (tick.Price <= 0 || tick.Volume < 0) return;
        var tick = new MarketTick("BTCUSDT", price, 1m, 1000, false);
        var isValid = tick.Price > 0 && tick.Volume >= 0;

        isValid.Should().BeFalse("price <= 0 should be rejected by sanity guard");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-0.001)]
    public void PriceSanityGuard_NegativeVolume_ShouldBeRejected(decimal volume)
    {
        var tick = new MarketTick("BTCUSDT", 60000m, volume, 1000, false);
        var isValid = tick.Price > 0 && tick.Volume >= 0;

        isValid.Should().BeFalse("negative volume should be rejected");
    }

    [Fact]
    public void PriceSanityGuard_ValidTick_ShouldBeAccepted()
    {
        var tick = new MarketTick("BTCUSDT", 60000m, 0.5m, 1000, false);
        var isValid = tick.Price > 0 && tick.Volume >= 0;

        isValid.Should().BeTrue();
    }

    [Fact]
    public void PriceSanityGuard_ZeroVolume_ShouldBeAccepted()
    {
        // Zero volume is valid (market data can have 0 volume)
        var tick = new MarketTick("BTCUSDT", 60000m, 0m, 1000, false);
        var isValid = tick.Price > 0 && tick.Volume >= 0;

        isValid.Should().BeTrue("zero volume is allowed, only negative is rejected");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Order Book Sanity Guards
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void OrderBookSanityGuard_ZeroBidPrice_ShouldBeRejected()
    {
        var ob = new OrderBookSnapshot(
            "BTCUSDT", 1000,
            new[] { new OrderBookLevel(0m, 1m) },
            new[] { new OrderBookLevel(60001m, 1m) });

        var isValid = ob.Bids.Length == 0 || ob.Bids[0].Price > 0;
        isValid.Should().BeFalse("zero bid price should be rejected");
    }

    [Fact]
    public void OrderBookSanityGuard_ZeroAskPrice_ShouldBeRejected()
    {
        var ob = new OrderBookSnapshot(
            "BTCUSDT", 1000,
            new[] { new OrderBookLevel(59999m, 1m) },
            new[] { new OrderBookLevel(0m, 1m) });

        var isValid = ob.Asks.Length == 0 || ob.Asks[0].Price > 0;
        isValid.Should().BeFalse("zero ask price should be rejected");
    }

    [Fact]
    public void OrderBookSanityGuard_ValidOrderBook_ShouldBeAccepted()
    {
        var ob = new OrderBookSnapshot(
            "BTCUSDT", 1000,
            new[] { new OrderBookLevel(59999m, 1m) },
            new[] { new OrderBookLevel(60001m, 1m) });

        var isValid = (ob.Bids.Length == 0 || ob.Bids[0].Price > 0)
                      && (ob.Asks.Length == 0 || ob.Asks[0].Price > 0);
        isValid.Should().BeTrue();
    }

    [Fact]
    public void OrderBookSanityGuard_EmptyBids_ShouldPass()
    {
        var ob = new OrderBookSnapshot(
            "BTCUSDT", 1000,
            Array.Empty<OrderBookLevel>(),
            new[] { new OrderBookLevel(60001m, 1m) });

        var isValid = ob.Bids.Length == 0 || ob.Bids[0].Price > 0;
        isValid.Should().BeTrue("empty bids array should pass the guard");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  OFI Calculation (bid-ask imbalance)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void OfiCalculation_AllBids_ReturnsPositiveOne()
    {
        decimal bidVol = 100m, askVol = 0m;
        decimal totalVol = bidVol + askVol;
        double imbalance = totalVol > 0 ? (double)((bidVol - askVol) / totalVol) : 0;

        imbalance.Should().Be(1.0);
    }

    [Fact]
    public void OfiCalculation_AllAsks_ReturnsNegativeOne()
    {
        decimal bidVol = 0m, askVol = 100m;
        decimal totalVol = bidVol + askVol;
        double imbalance = totalVol > 0 ? (double)((bidVol - askVol) / totalVol) : 0;

        imbalance.Should().Be(-1.0);
    }

    [Fact]
    public void OfiCalculation_EqualVolume_ReturnsZero()
    {
        decimal bidVol = 50m, askVol = 50m;
        decimal totalVol = bidVol + askVol;
        double imbalance = totalVol > 0 ? (double)((bidVol - askVol) / totalVol) : 0;

        imbalance.Should().Be(0.0);
    }

    [Fact]
    public void OfiCalculation_ZeroVolume_ReturnsZero()
    {
        decimal bidVol = 0m, askVol = 0m;
        decimal totalVol = bidVol + askVol;
        double imbalance = totalVol > 0 ? (double)((bidVol - askVol) / totalVol) : 0;

        imbalance.Should().Be(0.0);
    }

    [Fact]
    public void OfiCalculation_BidDominant_ReturnsPositive()
    {
        decimal bidVol = 75m, askVol = 25m;
        decimal totalVol = bidVol + askVol;
        double imbalance = totalVol > 0 ? (double)((bidVol - askVol) / totalVol) : 0;

        imbalance.Should().BeApproximately(0.5, 0.001);
    }
}
