using System.Collections.Concurrent;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Tracks a single symbol's open position state.
/// </summary>
public sealed record PositionState
{
    public string Symbol { get; init; } = "";
    public decimal EntryPrice { get; init; }
    public decimal Quantity { get; init; }
    public bool IsLong { get; init; }
    public DateTime OpenedAt { get; init; }

    /// <summary>
    /// Calculates unrealized P&amp;L at a given market price.
    /// </summary>
    public decimal UnrealizedPnl(decimal currentPrice)
    {
        var direction = IsLong ? 1m : -1m;
        return (currentPrice - EntryPrice) * Quantity * direction;
    }
}

/// <summary>
/// A high-performance, in-memory portfolio manager for real-time balance tracking.
/// Designed for low-latency HFT environments with per-symbol position tracking.
/// </summary>
public sealed class MemoryPortfolioManager
{
    private readonly ConcurrentDictionary<string, decimal> _balances = new();
    private readonly ConcurrentDictionary<string, decimal> _lockedBalances = new();
    private readonly ConcurrentDictionary<string, PositionState> _positions = new();

    // 🔥 HFT Optimization: Direct fields for most common quote assets to bypass ConcurrentDictionary lookups
    private decimal _usdtBalance;
    private decimal _tryBalance;
    private decimal _usdtLocked;
    private decimal _tryLocked;

    // P&L tracking
    private decimal _realizedPnl;
    private decimal _peakEquity;
    private decimal _initialEquity;

    public decimal RealizedPnl => _realizedPnl;
    public decimal PeakEquity => _peakEquity;

    public void SetBalance(string asset, decimal amount)
    {
        if (asset == "USDT") _usdtBalance = amount;
        else if (asset == "TRY") _tryBalance = amount;
        _balances[asset] = amount;

        // Track initial equity for drawdown calculation
        if (_initialEquity == 0 && asset == "USDT") _initialEquity = amount;
        if (amount > _peakEquity) _peakEquity = amount;
    }

    public void AddBalance(string asset, decimal delta)
    {
        if (asset == "USDT") InterlockedAdd(ref _usdtBalance, delta);
        else if (asset == "TRY") InterlockedAdd(ref _tryBalance, delta);
        _balances.AddOrUpdate(asset, delta, (k, v) => v + delta);
    }

    public decimal GetTotalBalance(string asset)
    {
        if (asset == "USDT") return _usdtBalance;
        if (asset == "TRY") return _tryBalance;
        return _balances.TryGetValue(asset, out var b) ? b : 0m;
    }

    public void Lock(string asset, decimal amount)
    {
        if (asset == "USDT") InterlockedAdd(ref _usdtLocked, amount);
        else if (asset == "TRY") InterlockedAdd(ref _tryLocked, amount);
        _lockedBalances.AddOrUpdate(asset, amount, (k, v) => v + amount);
    }

    public void Unlock(string asset, decimal amount)
    {
        if (asset == "USDT") InterlockedAdd(ref _usdtLocked, -amount);
        else if (asset == "TRY") InterlockedAdd(ref _tryLocked, -amount);
        _lockedBalances.AddOrUpdate(asset, 0m, (k, v) => Math.Max(0, v - amount));
    }

    public decimal GetAvailableBalance(string asset)
    {
        if (asset == "USDT") return Math.Max(0, _usdtBalance - _usdtLocked);
        if (asset == "TRY") return Math.Max(0, _tryBalance - _tryLocked);
        
        var total = GetTotalBalance(asset);
        var locked = _lockedBalances.TryGetValue(asset, out var l) ? l : 0m;
        return Math.Max(0, total - locked);
    }

    // --- Position Management ---

    /// <summary>Opens or updates a position for a symbol.</summary>
    public void OpenPosition(string symbol, decimal entryPrice, decimal quantity, bool isLong)
    {
        _positions[symbol] = new PositionState
        {
            Symbol = symbol,
            EntryPrice = entryPrice,
            Quantity = quantity,
            IsLong = isLong,
            OpenedAt = DateTime.UtcNow
        };
    }

    /// <summary>Closes a position and records realized P&amp;L.</summary>
    public decimal ClosePosition(string symbol, decimal exitPrice)
    {
        if (!_positions.TryRemove(symbol, out var pos))
            return 0m;

        var pnl = pos.UnrealizedPnl(exitPrice);
        InterlockedAdd(ref _realizedPnl, pnl);
        AddBalance("USDT", pnl);

        // Update peak equity
        var currentEquity = GetTotalBalance("USDT");
        if (currentEquity > _peakEquity)
            InterlockedAdd(ref _peakEquity, currentEquity - _peakEquity);

        return pnl;
    }

    /// <summary>Gets the current position for a symbol, or null if none.</summary>
    public PositionState? GetPosition(string symbol)
        => _positions.TryGetValue(symbol, out var pos) ? pos : null;

    /// <summary>Gets all open positions.</summary>
    public IReadOnlyDictionary<string, PositionState> GetAllPositions()
        => _positions;

    /// <summary>
    /// Total exposure = sum of all position notional values.
    /// </summary>
    public decimal GetTotalExposure()
    {
        decimal exposure = 0;
        foreach (var pos in _positions.Values)
            exposure += pos.EntryPrice * pos.Quantity;
        return exposure;
    }

    /// <summary>
    /// Current drawdown from peak equity as a percentage.
    /// </summary>
    public decimal GetDrawdownPercent()
    {
        if (_peakEquity <= 0) return 0;
        var currentEquity = GetTotalBalance("USDT");
        return (_peakEquity - currentEquity) / _peakEquity * 100;
    }

    private static void InterlockedAdd(ref decimal location, decimal value)
    {
        // Decimal is not natively supported by Interlocked.Add. 
        // We use a CompareExchange loop for thread-safety without heavy locks.
        decimal initial, computed;
        do
        {
            initial = location;
            computed = initial + value;
        } while (initial != Interlocked.CompareExchange(ref location, computed, initial));
    }

    public void Clear()
    {
        _balances.Clear();
        _lockedBalances.Clear();
        _positions.Clear();
        _realizedPnl = 0;
    }
}

