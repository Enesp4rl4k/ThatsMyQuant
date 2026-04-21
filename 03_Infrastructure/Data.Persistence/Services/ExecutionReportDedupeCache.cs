using System.Collections.Concurrent;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Bounded in-memory dedupe cache for fast suppression of repeated execution reports.
/// Key strategy: ExchangeOrderId + LastFillQuantity (+Symbol/+Status for safety).
/// </summary>
public sealed class ExecutionReportDedupeCache
{
    private readonly int _maxEntries;
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _fifo = new();

    public ExecutionReportDedupeCache(int maxEntries = 10_000)
    {
        _maxEntries = Math.Max(128, maxEntries);
    }

    public bool ShouldProcess(ExecutionReport report)
    {
        var key = BuildKey(report);
        if (_seen.TryAdd(key, 0))
        {
            _fifo.Enqueue(key);
            Trim();
            return true;
        }

        return false;
    }

    internal static string BuildKey(ExecutionReport report)
    {
        var exchangeOrderId = string.IsNullOrWhiteSpace(report.ExchangeOrderId) ? "no-order" : report.ExchangeOrderId.Trim();
        return $"{exchangeOrderId}|{report.LastFillQuantity}|{report.Symbol}|{report.Status}";
    }

    private void Trim()
    {
        while (_seen.Count > _maxEntries && _fifo.TryDequeue(out var oldest))
        {
            _seen.TryRemove(oldest, out _);
        }
    }
}
