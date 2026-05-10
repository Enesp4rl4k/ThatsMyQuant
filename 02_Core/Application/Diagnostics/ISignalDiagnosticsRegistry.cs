using System.Collections.Generic;

namespace ThatsMyQuant.Application.Diagnostics;

/// <summary>
/// In-process registry holding the latest <see cref="SignalDiagnosticSnapshot"/>
/// per (Tier, Key) so the worker can publish on every executor iteration and the
/// SignalR broadcaster can fan out the full panel on its own cadence (~3s).
/// </summary>
/// <remarks>
/// Implementations must be safe for concurrent <see cref="Publish"/> from
/// multiple executor threads (Funding, Pairs, EdgeLab tiers each tick on their
/// own scheduler). Reads (<see cref="GetAll"/>) happen from the broadcaster's
/// background loop and the REST controller; both can race with publishes — they
/// just need a consistent snapshot of whatever was committed at read time.
/// </remarks>
public interface ISignalDiagnosticsRegistry
{
    /// <summary>
    /// Replace the snapshot for (<paramref name="snapshot"/>.Tier,
    /// <paramref name="snapshot"/>.Key). Last-write-wins by timestamp — late
    /// publishes from a slow thread will not clobber a newer entry.
    /// </summary>
    void Publish(SignalDiagnosticSnapshot snapshot);

    /// <summary>
    /// Snapshot of all currently-tracked rows. Caller may sort/filter freely.
    /// </summary>
    IReadOnlyList<SignalDiagnosticSnapshot> GetAll();

    /// <summary>
    /// Optional helper for tier-scoped panels (e.g. funding-only view). Returns
    /// rows whose <see cref="SignalDiagnosticSnapshot.Tier"/> equals
    /// <paramref name="tier"/> (case-insensitive).
    /// </summary>
    IReadOnlyList<SignalDiagnosticSnapshot> GetForTier(string tier);

    /// <summary>
    /// Number of rows currently tracked. Cheap; mainly for health endpoints.
    /// </summary>
    int Count { get; }
}
