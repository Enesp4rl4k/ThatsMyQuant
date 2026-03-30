using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.Risk;

/// <summary>
/// High-performance, in-memory state machine tracking global portfolio exposure.
/// Replaces slow database lookups with O(1) lock-free memory access for the Risk hot-path.
/// </summary>
public sealed class MemoryPortfolioManager
{
    private readonly ConcurrentDictionary<string, decimal> _balances = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, decimal> _positions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<MemoryPortfolioManager> _logger;

    public MemoryPortfolioManager(ILogger<MemoryPortfolioManager> logger)
    {
        _logger = logger;
        
        // Identity Seed: Pre-warming common pairs to avoid dictionary bucket resizing under load
        _balances.TryAdd("USDT", 100000m); // Seed with mock institutional capital
        _balances.TryAdd("BTC", 0m);
        _balances.TryAdd("ETH", 0m);
    }

    /// <summary>
    /// Atomic update triggered by ExecutionReports (FIX/Websocket) or periodic syncs.
    /// </summary>
    public void SetBalance(string asset, decimal amount)
    {
        _balances[asset] = amount;
        _logger.LogDebug("Portfolio Sync: {Asset} balance updated to {Amount}", asset, amount);
    }

    /// <summary>
    /// Thread-safe localized balance retrieval in nanoseconds.
    /// </summary>
    public decimal GetAvailableBalance(string asset)
    {
        return _balances.TryGetValue(asset, out var balance) ? balance : 0m;
    }

    /// <summary>
    /// Returns total balance including locked funds.
    /// </summary>
    public decimal GetTotalBalance(string asset)
    {
        // Future: Track locked funds per pending limit orders
        return GetAvailableBalance(asset);
    }

    /// <summary>
    /// Tracks open quantity for a specific token symbol.
    /// </summary>
    public void UpdatePosition(string symbol, decimal deltaQuantity)
    {
        _positions.AddOrUpdate(symbol, deltaQuantity, (key, oldVal) => oldVal + deltaQuantity);
    }

    public decimal GetPosition(string symbol)
    {
        return _positions.TryGetValue(symbol, out var qty) ? qty : 0m;
    }
}
