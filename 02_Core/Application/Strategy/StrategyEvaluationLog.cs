using System.Collections.Concurrent;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// In-memory ring buffer that captures every strategy evaluation tick — the
/// "why didn't the bot trade?" trail. Strategies push entries on every cycle
/// (whether they decided to ENTER or REJECT). The web UI reads from this via
/// REST so the operator can see live diagnostics without grepping logs.
///
/// Capped at <see cref="Capacity"/> entries (oldest evicted). Threadsafe via
/// a ConcurrentQueue — no lock contention at the publisher's hot path.
///
/// Registered as a Singleton; resolved by both:
///   • PairsHybridExecutor / FundingHybridExecutor (publishers)
///   • SettingsEndpoints / Listener.Api (readers via /api/strategy/evaluations)
/// </summary>
public sealed class StrategyEvaluationLog
{
    public const int Capacity = 200;

    private readonly ConcurrentQueue<StrategyEvaluationEntry> _entries = new();

    public void Publish(StrategyEvaluationEntry entry)
    {
        _entries.Enqueue(entry);
        // Trim oldest if over capacity
        while (_entries.Count > Capacity && _entries.TryDequeue(out _)) { }
    }

    public IReadOnlyList<StrategyEvaluationEntry> Snapshot(int? limit = null)
    {
        var arr = _entries.ToArray();
        Array.Reverse(arr); // newest first
        return limit.HasValue && limit.Value < arr.Length
            ? arr[..limit.Value]
            : arr;
    }

    public IReadOnlyList<StrategyEvaluationEntry> SnapshotByStrategy(string strategy, int? limit = null)
    {
        var all = Snapshot();
        var filtered = all.Where(e => string.Equals(e.Strategy, strategy, StringComparison.OrdinalIgnoreCase));
        return limit.HasValue
            ? filtered.Take(limit.Value).ToArray()
            : filtered.ToArray();
    }
}

/// <summary>
/// Single evaluation tick. <c>Decision</c> is one of:
///   • "Enter"   — strategy emitted an entry signal
///   • "Exit"    — strategy emitted an exit signal for an open position
///   • "Reject"  — strategy looked but found nothing tradeable (most common)
///   • "Hold"    — open position held without action this tick
///   • "Error"   — evaluation threw / failed
/// </summary>
public sealed record StrategyEvaluationEntry(
    DateTime UtcTime,
    string Strategy,         // "Pairs", "FundingLong", "FundingShort", etc.
    string Symbol,           // "BTCUSDT" or "SOLUSDT/AVAXUSDT" for pairs
    string Decision,         // "Enter" | "Exit" | "Reject" | "Hold" | "Error"
    string Reason,           // human-readable: "z=0.42 < entry 2.0"
    IReadOnlyDictionary<string, double>? Metrics = null);
