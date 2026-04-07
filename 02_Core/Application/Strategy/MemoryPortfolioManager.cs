using System.Collections.Concurrent;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A high-performance, in-memory portfolio manager for real-time balance tracking.
/// Designed for low-latency HFT environments with per-symbol position tracking.
/// </summary>
public sealed class MemoryPortfolioManager : IPortfolioManager
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
        var position = new PositionState
        {
            Symbol = symbol,
            EntryPrice = entryPrice,
            Quantity = quantity,
            IsLong = isLong,
            OpenedAt = DateTime.UtcNow
        };
        position.UpdatePrice(entryPrice); // Initialize MAFE
        _positions[symbol] = position;
    }

    /// <summary>Closes a position and records realized P&L.</summary>
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
    /// Updates market prices for all open positions to calculate dynamic MFE/MAE.
    /// Recommended to be called from the TickPipeline or market data handler.
    /// </summary>
    public void UpdateMarketPrices(IReadOnlyDictionary<string, decimal> latestPrices)
    {
        foreach (var (symbol, price) in latestPrices)
        {
            if (_positions.TryGetValue(symbol, out var pos))
            {
                pos.UpdatePrice(price);
            }
        }
    }

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
        // Decimal is 128-bit and not supported by Interlocked.CompareExchange.
        // Use a lock-based CAS loop for thread-safety.
        lock (_decimalLock)
        {
            location += value;
        }
    }

    private static readonly object _decimalLock = new();

    public void Clear()
    {
        _balances.Clear();
        _lockedBalances.Clear();
        _positions.Clear();
        _realizedPnl = 0;
    }

    /// <summary>
    /// Updates the portfolio state based on a real-time execution report (Trade object).
    /// Handles partial fills, full fills, and cancellations.
    /// </summary>
    public void UpdateFromExecutionReport(Trade trade)
    {
        if (trade == null) return;

        if (trade.Status == TradeStatus.Executed || trade.LifecycleStatus == TradeLifecycleStatus.PartiallyFilled)
        {
            // Update or open position
            OpenPosition(
                trade.Symbol,
                trade.EntryPrice,
                trade.FilledQuantity > 0 ? trade.FilledQuantity : trade.Quantity,
                trade.Side == TradeSide.Buy);

            // If it's a full fill or closed, record P&L
            if (trade.IsClosed)
            {
                ClosePosition(trade.Symbol, trade.AverageExitPrice > 0 ? trade.AverageExitPrice : trade.ExitPrice);
            }
        }
        else if (trade.Status == TradeStatus.Rejected || trade.Status == TradeStatus.Failed)
        {
            // If an order was canceled, ensure we unlock the balance
            var quoteAsset = trade.Symbol.EndsWith("USDT") ? "USDT" : "TRY";
            var estimatedNotional = trade.EntryPrice * trade.Quantity;
            Unlock(quoteAsset, estimatedNotional);
            
            // If it was partially filled before cancellation, we keep the position as is 
            // unless the LifecycleStatus marks it as closed.
            if (trade.IsClosed)
            {
                _positions.TryRemove(trade.Symbol, out _);
            }
        }
    }

    /// <summary>
    /// Reconciles the in-memory balances with external source of truth (Exchange REST API).
    /// Used to catch drift from missed WebSocket events.
    /// </summary>
    public void Reconcile(IReadOnlyDictionary<string, (decimal Total, decimal Locked)> externalBalances)
    {
        foreach (var (asset, (total, locked)) in externalBalances)
        {
            SetBalance(asset, total);
            
            if (asset == "USDT") _usdtLocked = locked;
            else if (asset == "TRY") _tryLocked = locked;
            _lockedBalances[asset] = locked;
        }

        // Remove assets that are no longer in the external report
        foreach (var asset in _balances.Keys.ToList())
        {
            if (!externalBalances.ContainsKey(asset))
            {
                _balances.TryRemove(asset, out _);
                _lockedBalances.TryRemove(asset, out _);
            }
        }
    }
}
