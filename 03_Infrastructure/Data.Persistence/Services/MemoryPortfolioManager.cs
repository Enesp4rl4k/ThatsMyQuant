using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Threading;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Venue-aware, lock-free in-memory portfolio manager for execution hot paths.
/// Balances and reservations are maintained per venue/asset using atomic scaled longs.
/// </summary>
public sealed class MemoryPortfolioManager : ISharedPortfolioStateManager
{
    private const long BalanceScale = 100_000_000L;

    private static readonly Lazy<MemoryPortfolioManager> _instance =
        new(() => new MemoryPortfolioManager());

    public static MemoryPortfolioManager Instance => _instance.Value;

    private readonly ConcurrentDictionary<BalanceKey, AtomicBalanceCell> _balances = new();
    private readonly ConcurrentDictionary<PositionKey, AtomicPositionCell> _positions = new();

    private MemoryPortfolioManager() { }

    public decimal AvailableUsdt => GetAvailableBalance(VenueKind.Binance, "USDT");

    public void UpdateBalance(decimal delta) => AddBalanceDelta(VenueKind.Binance, "USDT", delta);

    public void SetBalance(decimal balance) => SetBalance(VenueKind.Binance, "USDT", balance);

    public bool TryConsumeUsdt(decimal required) => TryReserve(VenueKind.Binance, "USDT", required);

    public void SetBalance(VenueKind venue, string asset, decimal amount)
    {
        var cell = GetBalanceCell(venue, asset);
        cell.SetTotal(ToScaled(amount));
    }

    public void AddBalanceDelta(VenueKind venue, string asset, decimal delta)
    {
        var cell = GetBalanceCell(venue, asset);
        cell.AddTotal(ToScaled(delta));
    }

    public decimal GetTotalBalance(VenueKind venue, string asset)
    {
        return FromScaled(GetBalanceCell(venue, asset).TotalScaled);
    }

    public decimal GetAvailableBalance(VenueKind venue, string asset)
    {
        return FromScaled(GetBalanceCell(venue, asset).AvailableScaled);
    }

    public bool TryReserve(VenueKind venue, string asset, decimal amount)
    {
        if (amount <= 0m)
        {
            return true;
        }

        var cell = GetBalanceCell(venue, asset);
        return cell.TryReserve(ToScaled(amount));
    }

    public void ReleaseReservation(VenueKind venue, string asset, decimal amount)
    {
        if (amount <= 0m)
        {
            return;
        }

        var cell = GetBalanceCell(venue, asset);
        cell.Release(ToScaled(amount));
    }

    public void UpdatePosition(VenueKind venue, string symbol, decimal quantity)
    {
        var key = new PositionKey(venue, NormalizeSymbol(symbol));
        var cell = _positions.GetOrAdd(key, static keyValue => new AtomicPositionCell(keyValue.Symbol));
        cell.AddQuantity(quantity);
    }

    public decimal GetPosition(VenueKind venue, string symbol)
    {
        var key = new PositionKey(venue, NormalizeSymbol(symbol));
        return _positions.TryGetValue(key, out var cell) ? cell.Quantity : 0m;
    }

    public VenuePortfolioSnapshot GetVenueSnapshot(VenueKind venue)
    {
        var balances = _balances
            .Where(entry => entry.Key.Venue == venue)
            .ToDictionary(
                entry => entry.Key.Asset,
                entry => new VenueBalanceState(
                    FromScaled(entry.Value.TotalScaled),
                    FromScaled(entry.Value.ReservedScaled),
                    FromScaled(entry.Value.AvailableScaled),
                    entry.Value.UpdatedAtUtc));

        var positions = _positions
            .Where(entry => entry.Key.Venue == venue)
            .ToDictionary(
                entry => entry.Key.Symbol,
                entry => new VenuePositionState(
                    entry.Value.Symbol,
                    entry.Value.Quantity,
                    entry.Value.UpdatedAtUtc));

        return new VenuePortfolioSnapshot(
            venue,
            new ReadOnlyDictionary<string, VenueBalanceState>(balances),
            new ReadOnlyDictionary<string, VenuePositionState>(positions),
            DateTime.UtcNow);
    }

    public GlobalPortfolioSnapshot GetGlobalSnapshot()
    {
        var balances = _balances.ToDictionary(
            entry => entry.Key,
            entry => new VenueBalanceState(
                FromScaled(entry.Value.TotalScaled),
                FromScaled(entry.Value.ReservedScaled),
                FromScaled(entry.Value.AvailableScaled),
                entry.Value.UpdatedAtUtc));

        var positions = _positions.ToDictionary(
            entry => entry.Key,
            entry => new VenuePositionState(
                entry.Value.Symbol,
                entry.Value.Quantity,
                entry.Value.UpdatedAtUtc));

        return new GlobalPortfolioSnapshot(
            new ReadOnlyDictionary<BalanceKey, VenueBalanceState>(balances),
            new ReadOnlyDictionary<PositionKey, VenuePositionState>(positions),
            DateTime.UtcNow);
    }

    public decimal GetPosition(string symbol) => GetPosition(VenueKind.Binance, symbol);

    public (decimal Balance, IReadOnlyDictionary<string, decimal> Positions) GetSnapshot()
    {
        var snapshot = GetVenueSnapshot(VenueKind.Binance);
        return (
            snapshot.Balances.TryGetValue("USDT", out var usdt) ? usdt.Available : 0m,
            new ReadOnlyDictionary<string, decimal>(snapshot.Positions.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Quantity)));
    }

    public void UpdatePosition(string symbol, decimal quantity) => UpdatePosition(VenueKind.Binance, symbol, quantity);

    public void Clear()
    {
        _balances.Clear();
        _positions.Clear();
    }

    private AtomicBalanceCell GetBalanceCell(VenueKind venue, string asset)
    {
        var normalizedAsset = NormalizeAsset(asset);
        return _balances.GetOrAdd(new BalanceKey(venue, normalizedAsset), static _ => new AtomicBalanceCell());
    }

    private static string NormalizeAsset(string asset) =>
        string.IsNullOrWhiteSpace(asset) ? "UNKNOWN" : asset.Trim().ToUpperInvariant();

    private static string NormalizeSymbol(string symbol) =>
        string.IsNullOrWhiteSpace(symbol) ? "UNKNOWN" : symbol.Trim().ToUpperInvariant();

    private static long ToScaled(decimal value) =>
        decimal.ToInt64(decimal.Round(value * BalanceScale, 0, MidpointRounding.AwayFromZero));

    private static decimal FromScaled(long value) => value / (decimal)BalanceScale;

    private sealed class AtomicBalanceCell
    {
        private long _totalScaled;
        private long _reservedScaled;
        private long _updatedTicks;

        public long TotalScaled => Interlocked.Read(ref _totalScaled);

        public long ReservedScaled => Interlocked.Read(ref _reservedScaled);

        public long AvailableScaled => Math.Max(0L, TotalScaled - ReservedScaled);

        public DateTime UpdatedAtUtc => new(Interlocked.Read(ref _updatedTicks), DateTimeKind.Utc);

        public void SetTotal(long totalScaled)
        {
            Interlocked.Exchange(ref _totalScaled, totalScaled);
            Touch();
        }

        public void AddTotal(long deltaScaled)
        {
            Interlocked.Add(ref _totalScaled, deltaScaled);
            Touch();
        }

        public bool TryReserve(long requiredScaled)
        {
            long total;
            long reserved;
            long updatedReserved;
            do
            {
                total = Interlocked.Read(ref _totalScaled);
                reserved = Interlocked.Read(ref _reservedScaled);
                if (total - reserved < requiredScaled)
                {
                    return false;
                }

                updatedReserved = reserved + requiredScaled;
            }
            while (Interlocked.CompareExchange(ref _reservedScaled, updatedReserved, reserved) != reserved);

            Touch();
            return true;
        }

        public void Release(long releaseScaled)
        {
            long reserved;
            long updatedReserved;
            do
            {
                reserved = Interlocked.Read(ref _reservedScaled);
                updatedReserved = Math.Max(0L, reserved - releaseScaled);
            }
            while (Interlocked.CompareExchange(ref _reservedScaled, updatedReserved, reserved) != reserved);

            Touch();
        }

        private void Touch() => Interlocked.Exchange(ref _updatedTicks, DateTime.UtcNow.Ticks);
    }

    private sealed class AtomicPositionCell
    {
        private readonly string _symbol;
        private long _quantityScaled;
        private long _updatedTicks;

        public AtomicPositionCell(string symbol)
        {
            _symbol = symbol;
            Interlocked.Exchange(ref _updatedTicks, DateTime.UtcNow.Ticks);
        }

        public string Symbol => _symbol;

        public decimal Quantity => FromScaled(Interlocked.Read(ref _quantityScaled));

        public DateTime UpdatedAtUtc => new(Interlocked.Read(ref _updatedTicks), DateTimeKind.Utc);

        public void AddQuantity(decimal delta)
        {
            Interlocked.Add(ref _quantityScaled, ToScaled(delta));
            Interlocked.Exchange(ref _updatedTicks, DateTime.UtcNow.Ticks);
        }
    }
}
