using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Monitors real-time data freshness per symbol. When the last tick for a symbol
/// exceeds the staleness threshold, trading is blocked to prevent catastrophic mispricing.
/// Thread-safe for concurrent WebSocket feeds (Binance, OKX, etc.).
/// </summary>
public sealed class DataFreshnessMonitor
{
    private readonly ILogger<DataFreshnessMonitor> _logger;
    private readonly ConcurrentDictionary<string, long> _lastTickMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _staleAlerted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maximum age (in seconds) before data is considered stale. Default: 30s.
    /// If no tick arrives for a symbol within this window, trading is blocked.
    /// </summary>
    public int StaleThresholdSeconds { get; set; } = 30;

    /// <summary>
    /// Critical gap threshold (in seconds). If data is missing for this long,
    /// a kill-switch level alert is raised. Default: 300s (5 min).
    /// </summary>
    public int CriticalGapSeconds { get; set; } = 300;

    public DataFreshnessMonitor(ILogger<DataFreshnessMonitor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Record a tick arrival for a symbol. Called from TickPipeline on every incoming tick.
    /// </summary>
    public void RecordTick(string symbol, long timestampMs)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _lastTickMs[symbol] = nowMs;

        // Clear stale alert if we were previously alerting
        if (_staleAlerted.TryGetValue(symbol, out var wasStale) && wasStale)
        {
            _staleAlerted[symbol] = false;
            _logger.LogInformation("[DataFreshness] {Symbol} data feed recovered. Gap ended.", symbol);
        }
    }

    /// <summary>
    /// Record an order book update arrival for a symbol.
    /// </summary>
    public void RecordOrderBook(string symbol, long timestampMs)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _lastTickMs[symbol] = nowMs;

        if (_staleAlerted.TryGetValue(symbol, out var wasStale) && wasStale)
        {
            _staleAlerted[symbol] = false;
            _logger.LogInformation("[DataFreshness] {Symbol} data feed recovered via order book.", symbol);
        }
    }

    /// <summary>
    /// Check if a symbol's data is fresh enough to trade on.
    /// Returns (isFresh, gapSeconds, reason).
    /// </summary>
    public (bool IsFresh, double GapSeconds, string Reason) CheckFreshness(string symbol)
    {
        if (!_lastTickMs.TryGetValue(symbol, out var lastMs))
            return (false, double.MaxValue, $"No data ever received for {symbol}.");

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var gapSeconds = (nowMs - lastMs) / 1000.0;

        if (gapSeconds >= CriticalGapSeconds)
        {
            if (!_staleAlerted.TryGetValue(symbol, out var alerted) || !alerted)
            {
                _staleAlerted[symbol] = true;
                _logger.LogCritical(
                    "[DataFreshness] CRITICAL GAP for {Symbol}: {Gap:F1}s without data. " +
                    "All trading BLOCKED. Check exchange connectivity.",
                    symbol, gapSeconds);
            }
            return (false, gapSeconds, $"Critical data gap: {gapSeconds:F0}s without data for {symbol}.");
        }

        if (gapSeconds >= StaleThresholdSeconds)
        {
            if (!_staleAlerted.TryGetValue(symbol, out var alerted) || !alerted)
            {
                _staleAlerted[symbol] = true;
                _logger.LogWarning(
                    "[DataFreshness] Stale data for {Symbol}: {Gap:F1}s since last tick (threshold={Threshold}s).",
                    symbol, gapSeconds, StaleThresholdSeconds);
            }
            return (false, gapSeconds, $"Stale data: {gapSeconds:F0}s since last tick for {symbol}.");
        }

        return (true, gapSeconds, "Data is fresh.");
    }

    /// <summary>
    /// Get freshness status for all tracked symbols.
    /// </summary>
    public IReadOnlyDictionary<string, (bool IsFresh, double GapSeconds)> GetAllStatus()
    {
        var result = new Dictionary<string, (bool, double)>(StringComparer.OrdinalIgnoreCase);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var kvp in _lastTickMs)
        {
            var gapSeconds = (nowMs - kvp.Value) / 1000.0;
            result[kvp.Key] = (gapSeconds < StaleThresholdSeconds, gapSeconds);
        }

        return result;
    }

    /// <summary>
    /// Returns true if ANY tracked symbol has stale data.
    /// Useful for a global "safe to trade?" check.
    /// </summary>
    public bool HasAnyStaleData()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var kvp in _lastTickMs)
        {
            if ((nowMs - kvp.Value) / 1000.0 >= StaleThresholdSeconds)
                return true;
        }
        return false;
    }
}
