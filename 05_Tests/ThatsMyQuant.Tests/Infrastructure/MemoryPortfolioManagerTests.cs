using FluentAssertions;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Infrastructure;

public sealed class MemoryPortfolioManagerTests
{
    private readonly MemoryPortfolioManager _portfolio = MemoryPortfolioManager.Instance;

    public MemoryPortfolioManagerTests()
    {
        _portfolio.Clear();
    }

    [Fact]
    public async Task UpdateBalance_ShouldRemainAccurateUnderParallelWrites()
    {
        var tasks = Enumerable.Range(0, 250)
            .Select(_ => Task.Run(() => _portfolio.UpdateBalance(4m)))
            .ToArray();

        await Task.WhenAll(tasks);

        _portfolio.AvailableUsdt.Should().Be(1000m);
    }

    [Fact]
    public async Task TryConsumeUsdt_ShouldAtomicallyLimitSuccessfulReservations()
    {
        _portfolio.SetBalance(1000m);
        var successes = 0;

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() =>
            {
                if (_portfolio.TryConsumeUsdt(60m))
                {
                    Interlocked.Increment(ref successes);
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        successes.Should().Be(16);
        _portfolio.AvailableUsdt.Should().Be(40m);
    }

    [Fact]
    public async Task UpdatePosition_ShouldAccumulateConcurrentDeltas()
    {
        var tasks = Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => _portfolio.UpdatePosition("BTCUSDT", 0.05m)))
            .ToArray();

        await Task.WhenAll(tasks);

        _portfolio.GetPosition("BTCUSDT").Should().Be(5.00m);
    }

    [Fact]
    public void SharedState_ShouldKeepVenueBalancesAndPositionsIsolated()
    {
        _portfolio.SetBalance(VenueKind.Binance, "USDT", 1500m);
        _portfolio.SetBalance(VenueKind.Okx, "USDT", 800m);
        _portfolio.UpdatePosition(VenueKind.Binance, "BTCUSDT", 0.40m);
        _portfolio.UpdatePosition(VenueKind.Okx, "BTCUSDT", 0.15m);

        _portfolio.GetAvailableBalance(VenueKind.Binance, "USDT").Should().Be(1500m);
        _portfolio.GetAvailableBalance(VenueKind.Okx, "USDT").Should().Be(800m);
        _portfolio.GetPosition(VenueKind.Binance, "BTCUSDT").Should().Be(0.40m);
        _portfolio.GetPosition(VenueKind.Okx, "BTCUSDT").Should().Be(0.15m);
    }

    [Fact]
    public void GetVenueSnapshot_ShouldReturnOnlyRequestedVenueState()
    {
        _portfolio.SetBalance(VenueKind.Binance, "USDT", 1200m);
        _portfolio.TryReserve(VenueKind.Binance, "USDT", 300m);
        _portfolio.SetBalance(VenueKind.Okx, "USDT", 900m);
        _portfolio.UpdatePosition(VenueKind.Binance, "ETHUSDT", 2m);
        _portfolio.UpdatePosition(VenueKind.Okx, "ETHUSDT", 1m);

        var snapshot = _portfolio.GetVenueSnapshot(VenueKind.Binance);

        snapshot.Venue.Should().Be(VenueKind.Binance);
        snapshot.Balances.Should().ContainKey("USDT");
        snapshot.Balances["USDT"].Total.Should().Be(1200m);
        snapshot.Balances["USDT"].Reserved.Should().Be(300m);
        snapshot.Balances["USDT"].Available.Should().Be(900m);
        snapshot.Positions.Should().ContainKey("ETHUSDT");
        snapshot.Positions["ETHUSDT"].Quantity.Should().Be(2m);
        snapshot.Positions.Should().NotContainKey("BTCUSDT");
    }
}
