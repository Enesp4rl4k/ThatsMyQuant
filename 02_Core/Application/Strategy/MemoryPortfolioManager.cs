using System.Collections.Concurrent;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A high-performance, in-memory portfolio manager for real-time balance tracking.
/// Designed for low-latency HFT environments.
/// </summary>
public sealed class MemoryPortfolioManager : IPortfolioManager
{
    private readonly ConcurrentDictionary<string, decimal> _balances = new();
    private readonly ConcurrentDictionary<string, decimal> _lockedBalances = new();

    public void SetBalance(string asset, decimal amount)
    {
        _balances[asset] = amount;
    }

    public void AddBalance(string asset, decimal delta)
    {
        _balances.AddOrUpdate(asset, delta, (k, v) => v + delta);
    }

    public decimal GetTotalBalance(string asset)
    {
        return _balances.TryGetValue(asset, out var b) ? b : 0m;
    }

    public void Lock(string asset, decimal amount)
    {
        _lockedBalances.AddOrUpdate(asset, amount, (k, v) => v + amount);
    }

    public void Unlock(string asset, decimal amount)
    {
        _lockedBalances.AddOrUpdate(asset, 0m, (k, v) => Math.Max(0, v - amount));
    }

    public decimal GetAvailableBalance(string asset)
    {
        var total = GetTotalBalance(asset);
        var locked = _lockedBalances.TryGetValue(asset, out var l) ? l : 0m;
        return Math.Max(0, total - locked);
    }

    public void Clear()
    {
        _balances.Clear();
        _lockedBalances.Clear();
    }
}
