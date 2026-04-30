using System.Collections.Concurrent;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Process-wide ledger of symbols where reconciliation has detected an open OKX
/// position that the bot does not track internally ("ghost orphan"). While a symbol
/// is in this registry, executors must REFUSE to open new positions on it — opening
/// a second position on a symbol where we already have an unaccounted position would
/// over-leverage the account and create cascading reconciliation noise.
///
/// This is the safe, reversible half of the "adopt-or-halt" reconciliation pattern.
/// Auto-close was deliberately NOT chosen because the orphan position may be a user's
/// manually-opened position (OKX UI, mobile app, etc.); flat-closing it would destroy
/// their work. The operator is alerted and decides: cancel on OKX, or signal "adopt"
/// via a future Telegram command. When reconciliation observes the symbol no longer
/// orphan (operator closed it on OKX), it is automatically removed from the registry.
/// </summary>
public static class OrphanHaltRegistry
{
    private static readonly ConcurrentDictionary<string, DateTime> _halted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Marks a symbol as orphan-halted. Idempotent — repeated calls refresh the timestamp.
    /// </summary>
    public static void Halt(string symbol, DateTime detectedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        _halted[symbol] = detectedAtUtc;
    }

    /// <summary>True when reconciliation currently flags this symbol as having an OKX-side orphan.</summary>
    public static bool IsHalted(string symbol)
        => !string.IsNullOrWhiteSpace(symbol) && _halted.ContainsKey(symbol);

    /// <summary>Removes the halt for a symbol. Called when reconciliation no longer observes the orphan.</summary>
    public static void Clear(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        _halted.TryRemove(symbol, out _);
    }

    /// <summary>Snapshot of currently halted symbols (with the UTC time each was first detected).</summary>
    public static IReadOnlyDictionary<string, DateTime> Snapshot()
        => _halted.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
}
