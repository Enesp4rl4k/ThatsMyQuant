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
public sealed class VenuePortfolioManager : ISharedPortfolioStateManager, IPortfolioManager
{
    private const long BalanceScale = 100_000_000L;
    private long _peakEquityScaled = 0;
    // [2026-05-13] _initialEquityScaled was declared but never read (CS0414). Kept as a
    // future hook for "fixed-anchor drawdown" mode; suppress the warning until that
    // code path lands. Removing the field outright would lose the design intent.
#pragma warning disable CS0414
    private long _initialEquityScaled = 0;
#pragma warning restore CS0414

    private readonly ConcurrentDictionary<BalanceKey, AtomicBalanceCell> _balances = new();
    private readonly ConcurrentDictionary<PositionKey, AtomicPositionCell> _positions = new();
    private readonly ConcurrentDictionary<OrderReservationKey, long> _orderReservations = new();

    /// <summary>
    /// Register via <c>AddThatsMyQuantInfrastructure</c> or construct in tests; avoid shared static state.
    /// </summary>
    public VenuePortfolioManager() { }

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

    public bool TryReserveForOrder(VenueKind venue, string asset, string orderId, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return TryReserve(venue, asset, amount);
        }

        if (amount <= 0m)
        {
            return true;
        }

        if (!TryReserve(venue, asset, amount))
        {
            return false;
        }

        var scaled = ToScaled(amount);
        var reservationKey = new OrderReservationKey(venue, NormalizeAsset(asset), orderId.Trim());
        _orderReservations.AddOrUpdate(reservationKey, scaled, (_, existing) => existing + scaled);
        return true;
    }

    public void ReleaseReservationForOrder(VenueKind venue, string asset, string orderId, decimal amount)
    {
        if (amount <= 0m)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(orderId))
        {
            ReleaseReservation(venue, asset, amount);
            return;
        }

        var reservationKey = new OrderReservationKey(venue, NormalizeAsset(asset), orderId.Trim());
        var releaseScaled = ToScaled(amount);
        while (true)
        {
            if (!_orderReservations.TryGetValue(reservationKey, out var trackedScaled) || trackedScaled <= 0L)
            {
                return;
            }

            var actualRelease = Math.Min(trackedScaled, releaseScaled);
            var updated = trackedScaled - actualRelease;
            if (_orderReservations.TryUpdate(reservationKey, updated, trackedScaled))
            {
                if (updated <= 0L)
                {
                    _orderReservations.TryRemove(reservationKey, out _);
                }

                GetBalanceCell(venue, asset).Release(actualRelease);
                return;
            }
        }
    }

    public void ReleaseAllReservationsForOrder(VenueKind venue, string asset, string orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return;
        }

        var reservationKey = new OrderReservationKey(venue, NormalizeAsset(asset), orderId.Trim());
        if (_orderReservations.TryRemove(reservationKey, out var trackedScaled) && trackedScaled > 0L)
        {
            GetBalanceCell(venue, asset).Release(trackedScaled);
        }
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
        _orderReservations.Clear();
        Interlocked.Exchange(ref _peakEquityScaled, 0);
    }

    // --- IPortfolioManager Bridge (Defaults to Binance venue) ---

    public decimal GetDrawdownPercent()
    {
        var snapshot = GetGlobalSnapshot();
        var totalEquityScaled = ToScaled(snapshot.TotalUsdtValue);
        
        long currentPeak;
        do
        {
            currentPeak = Interlocked.Read(ref _peakEquityScaled);
            if (totalEquityScaled <= currentPeak) break;
        } while (Interlocked.CompareExchange(ref _peakEquityScaled, totalEquityScaled, currentPeak) != currentPeak);
        
        if (currentPeak <= 0) return 0;
        return (decimal)(currentPeak - totalEquityScaled) / currentPeak;
    }

    public decimal GetAvailableBalance(string asset) => GetAvailableBalance(VenueKind.Binance, asset);
    public void SetBalance(string asset, decimal amount) => SetBalance(VenueKind.Binance, asset, amount);
    public void AddBalance(string asset, decimal delta) => AddBalanceDelta(VenueKind.Binance, asset, delta);
    public void Lock(string asset, decimal amount) => TryReserve(VenueKind.Binance, asset, amount);
    public void Unlock(string asset, decimal amount) => ReleaseReservation(VenueKind.Binance, asset, amount);
    
    public void OpenPosition(string symbol, decimal entryPrice, decimal quantity, bool isLong) 
        => UpdatePosition(VenueKind.Binance, symbol, isLong ? quantity : -quantity);

    public decimal ClosePosition(string symbol, decimal exitPrice) 
    {
        var key = new PositionKey(VenueKind.Binance, NormalizeSymbol(symbol));
        _positions.TryRemove(key, out _);
        return 0; // P&L tracking not implemented in this bridge yet
    }

    public PositionState? GetPosition(string symbol) 
    {
        var qty = GetPosition(VenueKind.Binance, symbol);
        if (qty == 0) return null;
        return new PositionState { Symbol = symbol, Quantity = Math.Abs(qty), IsLong = qty > 0 };
    }

    public IReadOnlyDictionary<string, PositionState> GetAllPositions()
    {
        return _positions
            .Where(p => p.Key.Venue == VenueKind.Binance)
            .ToDictionary(p => p.Key.Symbol, p => new PositionState 
            { 
                Symbol = p.Key.Symbol, 
                Quantity = Math.Abs(p.Value.Quantity), 
                IsLong = p.Value.Quantity > 0 
            });
    }

    public void Reconcile(IReadOnlyDictionary<string, (decimal Total, decimal Locked)> externalBalances)
    {
        foreach (var (asset, state) in externalBalances)
        {
            SetBalance(VenueKind.Binance, asset, state.Total);
            // Internal reservation tracking is separate from exchange locked funds in this model
        }
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

    private readonly record struct OrderReservationKey(VenueKind Venue, string Asset, string OrderId);
}
