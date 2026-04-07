using FluentAssertions;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Infrastructure;

[Collection("MemoryPortfolioManager")]
public class PortfolioManagerConcurrencyTests
{
    private readonly MemoryPortfolioManager _sut = new();

    public PortfolioManagerConcurrencyTests()
    {
        _sut.Clear();
    }

    [Fact]
    public async Task TryReserve_ConcurrentReservations_ShouldNotOverspend()
    {
        // Arrange: Set balance to 10,000 USDT
        _sut.SetBalance(VenueKind.Binance, "USDT", 10_000m);

        // Act: 100 concurrent attempts to reserve 200 each (total 20,000 > balance)
        var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
            _sut.TryReserve(VenueKind.Binance, "USDT", 200m)
        ));

        var results = await Task.WhenAll(tasks);

        // Assert: Only 50 should succeed (50 * 200 = 10,000)
        var successCount = results.Count(r => r);
        successCount.Should().Be(50, "exactly 50 reservations of 200 should fit in 10,000 balance");

        var available = _sut.GetAvailableBalance(VenueKind.Binance, "USDT");
        available.Should().Be(0m, "all balance should be reserved");
    }

    [Fact]
    public async Task AddBalanceDelta_ConcurrentIncrements_ShouldBeAtomic()
    {
        // Arrange
        _sut.SetBalance(VenueKind.Binance, "USDT", 0m);

        // Act: 1000 concurrent +1 increments
        var tasks = Enumerable.Range(0, 1000).Select(_ => Task.Run(() =>
            _sut.AddBalanceDelta(VenueKind.Binance, "USDT", 1m)
        ));

        await Task.WhenAll(tasks);

        // Assert: Total should be exactly 1000
        var total = _sut.GetTotalBalance(VenueKind.Binance, "USDT");
        total.Should().Be(1000m, "1000 atomic increments of 1 should total 1000");
    }

    [Fact]
    public async Task TryReserve_AndRelease_ConcurrentMixedOps_ShouldMaintainConsistency()
    {
        // Arrange
        _sut.SetBalance(VenueKind.Binance, "USDT", 5_000m);

        // Act: Concurrent reserve and release operations
        var reserveTasks = Enumerable.Range(0, 50).Select(_ => Task.Run(() =>
        {
            if (_sut.TryReserve(VenueKind.Binance, "USDT", 100m))
            {
                // Simulate some work then release
                Thread.SpinWait(100);
                _sut.ReleaseReservation(VenueKind.Binance, "USDT", 100m);
                return true;
            }
            return false;
        }));

        await Task.WhenAll(reserveTasks);

        // Assert: All reservations were released, balance should be fully available
        var available = _sut.GetAvailableBalance(VenueKind.Binance, "USDT");
        available.Should().Be(5_000m, "all reservations were released so full balance should be available");
    }

    [Fact]
    public void UpdatePosition_MultipleSymbols_ShouldIsolateCorrectly()
    {
        // Arrange
        _sut.SetBalance(VenueKind.Binance, "USDT", 50_000m);

        // Act
        _sut.UpdatePosition(VenueKind.Binance, "BTCUSDT", 0.5m);
        _sut.UpdatePosition(VenueKind.Binance, "ETHUSDT", 10m);
        _sut.UpdatePosition(VenueKind.Okx, "BTCUSDT", 0.3m);

        // Assert
        _sut.GetPosition(VenueKind.Binance, "BTCUSDT").Should().Be(0.5m);
        _sut.GetPosition(VenueKind.Binance, "ETHUSDT").Should().Be(10m);
        _sut.GetPosition(VenueKind.Okx, "BTCUSDT").Should().Be(0.3m);
    }

    [Fact]
    public void GetVenueSnapshot_ShouldReturnConsistentState()
    {
        // Arrange
        _sut.SetBalance(VenueKind.Binance, "USDT", 25_000m);
        _sut.TryReserve(VenueKind.Binance, "USDT", 5_000m);
        _sut.UpdatePosition(VenueKind.Binance, "BTCUSDT", 0.1m);

        // Act
        var snapshot = _sut.GetVenueSnapshot(VenueKind.Binance);

        // Assert
        snapshot.Balances.Should().ContainKey("USDT");
        snapshot.Balances["USDT"].Total.Should().Be(25_000m);
        snapshot.Balances["USDT"].Reserved.Should().Be(5_000m);
        snapshot.Balances["USDT"].Available.Should().Be(20_000m);
        snapshot.Positions.Should().ContainKey("BTCUSDT");
        snapshot.Positions["BTCUSDT"].Quantity.Should().Be(0.1m);
    }

    [Fact]
    public void TryReserve_ZeroAmount_ShouldReturnTrue()
    {
        _sut.SetBalance(VenueKind.Binance, "USDT", 1000m);
        _sut.TryReserve(VenueKind.Binance, "USDT", 0m).Should().BeTrue();
    }

    [Fact]
    public void TryReserve_NegativeAmount_ShouldReturnTrue()
    {
        _sut.SetBalance(VenueKind.Binance, "USDT", 1000m);
        _sut.TryReserve(VenueKind.Binance, "USDT", -50m).Should().BeTrue();
    }

    [Fact]
    public void TryReserve_ExceedsBalance_ShouldReturnFalse()
    {
        _sut.SetBalance(VenueKind.Binance, "USDT", 100m);
        _sut.TryReserve(VenueKind.Binance, "USDT", 101m).Should().BeFalse();
    }
}
