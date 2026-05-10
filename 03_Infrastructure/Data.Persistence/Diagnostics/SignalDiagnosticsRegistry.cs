using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ThatsMyQuant.Application.Diagnostics;

namespace ThatsMyQuant.Infrastructure.Diagnostics;

/// <summary>
/// In-memory, concurrent-safe registry of the latest signal diagnostic snapshots.
/// One row per (Tier, Key) — the worker overwrites on every publish; readers
/// always see the most recently committed snapshot.
/// </summary>
/// <remarks>
/// Last-write-wins is enforced by <see cref="SignalDiagnosticSnapshot.UtcTimestamp"/>:
/// a publish whose timestamp is older than the entry already in the dictionary is
/// silently dropped. This protects against thread-scheduling races where a slow
/// thread that started its evaluation first commits after a faster thread.
///
/// We do NOT persist anything — the registry is intentionally ephemeral so a worker
/// restart resets diagnostics to "nothing observed yet" rather than showing stale
/// data from before the restart.
/// </remarks>
public sealed class SignalDiagnosticsRegistry : ISignalDiagnosticsRegistry
{
    private readonly ConcurrentDictionary<(string Tier, string Key), SignalDiagnosticSnapshot> _store
        = new();

    public void Publish(SignalDiagnosticSnapshot snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.Tier))
            throw new ArgumentException("Tier required", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.Key))
            throw new ArgumentException("Key required", nameof(snapshot));

        var k = (snapshot.Tier, snapshot.Key);
        _store.AddOrUpdate(
            k,
            _ => snapshot,
            (_, existing) => snapshot.UtcTimestamp >= existing.UtcTimestamp ? snapshot : existing);
    }

    public IReadOnlyList<SignalDiagnosticSnapshot> GetAll()
        => _store.Values.ToList();

    public IReadOnlyList<SignalDiagnosticSnapshot> GetForTier(string tier)
    {
        if (string.IsNullOrWhiteSpace(tier))
            return Array.Empty<SignalDiagnosticSnapshot>();

        return _store.Values
            .Where(s => string.Equals(s.Tier, tier, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public int Count => _store.Count;
}
