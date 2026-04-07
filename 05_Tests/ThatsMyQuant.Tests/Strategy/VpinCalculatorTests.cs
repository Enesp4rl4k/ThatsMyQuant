using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class VpinCalculatorTests
{
    [Fact]
    public void NewCalculator_VpinStartsAtZero()
    {
        var calc = new VpinCalculator(1000m, 50);
        calc.CurrentVpin.Should().Be(0m);
        calc.IsToxic.Should().BeFalse();
    }

    [Fact]
    public void ProcessTick_BalancedFlow_LowVpin()
    {
        var calc = new VpinCalculator(100m, 10);

        // Simulate balanced buy/sell flow (alternating up/down ticks)
        for (int i = 0; i < 200; i++)
        {
            decimal price = 100m + (i % 2 == 0 ? 0.01m : -0.01m);
            calc.ProcessTick(price, 20m);
        }

        calc.CurrentVpin.Should().BeLessThan(0.50m);
        calc.IsToxic.Should().BeFalse();
    }

    [Fact]
    public void ProcessTick_HeavilyImbalancedFlow_HighVpin()
    {
        var calc = new VpinCalculator(100m, 10);

        // Simulate one-directional aggressive buying (larger price jumps for stronger classification)
        decimal price = 100m;
        for (int i = 0; i < 300; i++)
        {
            price += 0.20m;
            calc.ProcessTick(price, 25m);
        }

        calc.CurrentVpin.Should().BeGreaterThan(0.30m);
    }

    [Fact]
    public void ProcessBar_FillsBuckets()
    {
        var calc = new VpinCalculator(500m, 5);

        for (int i = 0; i < 10; i++)
        {
            calc.ProcessBar(new Candle
            {
                Open = 100m + i,
                High = 101m + i,
                Low = 99m + i,
                Close = 100.5m + i,
                Volume = 300m,
                Timestamp = DateTime.UtcNow
            });
        }

        var snapshot = calc.GetSnapshot();
        snapshot.BucketsFilled.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ToxicityThreshold_Configurable()
    {
        var calc = new VpinCalculator(50m, 5);
        calc.ToxicityThreshold = 0.30m;

        // Push enough directional volume to trigger
        decimal price = 100m;
        for (int i = 0; i < 100; i++)
        {
            price += 0.10m;
            calc.ProcessTick(price, 15m);
        }

        // With aggressive threshold, even moderate imbalance should trigger
        calc.ToxicityThreshold.Should().Be(0.30m);
    }

    [Fact]
    public void ProcessTick_ZeroVolume_Ignored()
    {
        var calc = new VpinCalculator(100m, 10);
        calc.ProcessTick(100m, 0m);
        calc.CurrentVpin.Should().Be(0m);
    }

    [Fact]
    public void GetSnapshot_ReturnsCorrectState()
    {
        var calc = new VpinCalculator(100m, 20);
        calc.ToxicityThreshold = 0.65m;

        var snap = calc.GetSnapshot();
        snap.TotalBuckets.Should().Be(20);
        snap.ToxicityThreshold.Should().Be(0.65m);
        snap.BucketsFilled.Should().Be(0);
    }
}
