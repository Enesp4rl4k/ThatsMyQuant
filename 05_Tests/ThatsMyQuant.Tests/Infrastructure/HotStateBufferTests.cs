using FluentAssertions;
using ThatsMyQuant.Application.HftInfrastructure;
using Xunit;

namespace ThatsMyQuant.Tests.Infrastructure;

public class HotStateBufferTests
{
    [Fact]
    public void UpdateQuote_ReadBackCorrectValues()
    {
        var buffer = new HotStateBuffer();

        buffer.UpdateQuote(99.95m, 100.05m);

        buffer.ReadBestBid().Should().Be(99.95m);
        buffer.ReadBestAsk().Should().Be(100.05m);
        buffer.ReadTickSequence().Should().Be(1);
    }

    [Fact]
    public void UpdateOfi_ClampsToRange()
    {
        var buffer = new HotStateBuffer();

        buffer.UpdateOfi(0.75);
        buffer.ReadOfi().Should().BeApproximately(0.75, 0.001);

        buffer.UpdateOfi(-1.5); // Should clamp to -1.0
        buffer.ReadOfi().Should().BeApproximately(-1.0, 0.001);

        buffer.UpdateOfi(2.0); // Should clamp to 1.0
        buffer.ReadOfi().Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void UpdateVpin_ClampsToRange()
    {
        var buffer = new HotStateBuffer();

        buffer.UpdateVpin(0.65m);
        buffer.ReadVpin().Should().Be(0.65m);

        buffer.UpdateVpin(1.5m); // Clamp to 1.0
        buffer.ReadVpin().Should().Be(1.0m);
    }

    [Fact]
    public void TradingAllowed_DefaultTrue()
    {
        var buffer = new HotStateBuffer();
        buffer.IsTradingAllowed().Should().BeTrue();
    }

    [Fact]
    public void SetKillSwitch_DisablesTrading()
    {
        var buffer = new HotStateBuffer();

        buffer.SetKillSwitch(true);
        buffer.IsKillSwitchArmed().Should().BeTrue();

        buffer.SetKillSwitch(false);
        buffer.IsKillSwitchArmed().Should().BeFalse();
    }

    [Fact]
    public void UpdateDirectionalBias_ReadBack()
    {
        var buffer = new HotStateBuffer();

        buffer.UpdateDirectionalBias(0.85f, 72f);

        buffer.ReadDirectionalBias().Should().BeApproximately(0.85f, 0.01f);
        buffer.ReadPredictionConfidence().Should().BeApproximately(72f, 0.1f);
    }

    [Fact]
    public void UpdateRegimeParameters_ReadBack()
    {
        var buffer = new HotStateBuffer();

        buffer.UpdateRegimeParameters(1, 2.5, 4.0);

        buffer.ReadCurrentRegime().Should().Be(1); // Trending
        buffer.ReadStopLossMultiplier().Should().BeApproximately(2.5, 0.01);
        buffer.ReadTakeProfitMultiplier().Should().BeApproximately(4.0, 0.01);
    }

    [Fact]
    public void IsDataStale_WhenNoTicksReceived_ReturnsTrue()
    {
        var buffer = new HotStateBuffer();
        buffer.IsDataStale(TimeSpan.FromMilliseconds(500)).Should().BeTrue();
    }

    [Fact]
    public void IsDataStale_AfterRecentTick_ReturnsFalse()
    {
        var buffer = new HotStateBuffer();
        buffer.UpdateQuote(100m, 101m);

        buffer.IsDataStale(TimeSpan.FromSeconds(5)).Should().BeFalse();
    }

    [Fact]
    public void TakeSnapshot_ReturnsCompleteState()
    {
        var buffer = new HotStateBuffer();
        buffer.UpdateQuote(50.50m, 50.60m);
        buffer.UpdateOfi(0.3);
        buffer.UpdateVpin(0.45m);
        buffer.SetTradingAllowed(true);
        buffer.UpdateSafePositionSize(0.05m);
        buffer.UpdateDrawdown(2.5m);
        buffer.UpdateDirectionalBias(0.6f, 85f);
        buffer.UpdateRegimeParameters(2, 1.5, 2.0);

        var snap = buffer.TakeSnapshot();

        snap.BestBid.Should().Be(50.50m);
        snap.BestAsk.Should().Be(50.60m);
        snap.Ofi.Should().BeApproximately(0.3, 0.001);
        snap.Vpin.Should().Be(0.45m);
        snap.TradingAllowed.Should().BeTrue();
        snap.SafePositionSize.Should().Be(0.05m);
        snap.DrawdownPercent.Should().Be(2.5m);
        snap.CurrentRegime.Should().Be(2);
        snap.StopLossMultiplier.Should().BeApproximately(1.5, 0.01);
    }

    [Fact]
    public async Task ConcurrentUpdates_NoCorruption()
    {
        var buffer = new HotStateBuffer();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Simulate Red Path: rapid quote updates
        var redTask = Task.Run(() =>
        {
            decimal price = 100m;
            while (!cts.IsCancellationRequested)
            {
                price += 0.01m;
                buffer.UpdateQuote(price - 0.05m, price + 0.05m);
                buffer.UpdateOfi(Math.Sin((double)price) * 0.5);
            }
        });

        // Simulate Yellow Path: periodic risk updates
        var yellowTask = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                buffer.SetTradingAllowed(true);
                buffer.UpdateSafePositionSize(0.03m);
                buffer.UpdateDrawdown(1.5m);
                Thread.Sleep(1);
            }
        });

        // Simulate Blue Path: infrequent regime updates
        var blueTask = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                buffer.UpdateRegimeParameters(1, 2.0, 3.0);
                Thread.Sleep(50);
            }
        });

        // Reader: continuously read without crashing
        var readerTask = Task.Run(() =>
        {
            int reads = 0;
            while (!cts.IsCancellationRequested)
            {
                var snap = buffer.TakeSnapshot();
                snap.BestAsk.Should().BeGreaterThanOrEqualTo(snap.BestBid);
                reads++;
            }
            return reads;
        });

        await Task.WhenAll(redTask, yellowTask, blueTask, readerTask).WaitAsync(TimeSpan.FromSeconds(3));

        // Verify final state is consistent
        var finalSnap = buffer.TakeSnapshot();
        finalSnap.BestAsk.Should().BeGreaterThan(0);
        finalSnap.TickSequence.Should().BeGreaterThan(0);
    }
}
