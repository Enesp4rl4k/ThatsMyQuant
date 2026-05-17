using ThatsMyQuant.Contracts.Interfaces;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// 2026-05-07 (Sprint 3.3 — Master Plan v3) — REPLACED demo stub with
/// per-symbol position diff. Old version compared a single decimal
/// `localPositionBtc` to `account.Balance` (account net wealth, not position
/// quantity) with explicit "Simplified for this demo" comment — never produced
/// a meaningful drift signal. Audit flagged this as the largest unwired safety
/// gap.
///
/// New behaviour:
/// 1. Caller passes the bot's internal expected open positions (per symbol +
///    qty + side).
/// 2. Service pulls live positions via <see cref="IExchangePositionReader"/>.
/// 3. Per-symbol diff:
///      - Position present in venue but not in internal book = ORPHAN.
///      - Position expected internally but absent at venue = PHANTOM.
///      - Both present, |Δqty| / max(venue, internal) > tolerance = DRIFT.
/// 4. Any of the three categories triggers HARD HALT semantics in the
///    return tuple — caller is expected to suppress all new entries until
///    a human manually clears the state.
///
/// Tolerance default 1% (0.01). Operationally: tighter than this catches
/// rounding noise from contract→base unit conversion; looser admits real
/// position drift after a partial-fill reorder bug.
/// </summary>
public class ReconciliationAgent
{
    private readonly IExchangeService _exchange;
    private readonly ILogger<ReconciliationAgent> _logger;
    private readonly decimal _tolerance;

    public ReconciliationAgent(
        IExchangeService exchange,
        ILogger<ReconciliationAgent> logger,
        decimal tolerance = 0.01m)  // 1% drift default
    {
        _exchange = exchange;
        _logger = logger;
        _tolerance = tolerance;
    }

    /// <summary>
    /// Legacy single-symbol path retained for callers that haven't migrated.
    /// Prefer the new <see cref="VerifyPositionsAsync"/> overload.
    /// </summary>
    public async Task<(bool InSync, string Message)> VerifyIntegrityAsync(decimal localPositionBtc)
    {
        try
        {
            _logger.LogInformation("[RECO] Legacy single-symbol audit (BTC qty {Q})", localPositionBtc);
            var account = await _exchange.GetAccountStatusAsync();
            decimal remotePosition = account.Balance;
            decimal drift = Math.Abs(localPositionBtc - remotePosition);
            if (drift > _tolerance)
            {
                _logger.LogCritical(
                    "[HALT] LEDGER DRIFT (legacy path)! Local: {Local}, Remote-balance: {Remote}, Drift: {Drift}",
                    localPositionBtc, remotePosition, drift);
                return (false, $"CRITICAL: Exchange state desynchronized by {drift} units. Halting for manual audit.");
            }
            return (true, "Legacy single-symbol path: states in sync.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RECO] Failed to reach exchange for legacy audit.");
            return (false, "Inaccessible exchange state. Strategy-Lock engaged.");
        }
    }

    /// <summary>
    /// Per-symbol reconciliation. Pass the bot's expected open positions; this
    /// pulls the live venue positions and returns a structured diff plus the
    /// halt verdict.
    /// </summary>
    public async Task<ReconciliationResult> VerifyPositionsAsync(
        IReadOnlyList<ExpectedPosition> expected,
        IExchangePositionReader? positionReader,
        CancellationToken ct = default)
    {
        if (positionReader is null)
        {
            return new ReconciliationResult(
                InSync: true,  // can't reconcile without venue reader; don't halt the bot
                Reason: "No IExchangePositionReader registered — reconciliation skipped.",
                Drifts: Array.Empty<PositionDrift>(),
                Orphans: Array.Empty<ExchangePositionSnapshot>(),
                Phantoms: Array.Empty<ExpectedPosition>());
        }

        IReadOnlyList<ExchangePositionSnapshot> venuePositions;
        try
        {
            venuePositions = await positionReader.GetOpenPositionsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RECO] GetOpenPositionsAsync failed.");
            // CONSERVATIVE: fail closed when we can't see the venue. Caller halts.
            return new ReconciliationResult(
                InSync: false,
                Reason: $"Venue position fetch failed: {ex.Message}. Halt new entries until reachable.",
                Drifts: Array.Empty<PositionDrift>(),
                Orphans: Array.Empty<ExchangePositionSnapshot>(),
                Phantoms: Array.Empty<ExpectedPosition>());
        }

        // Index venue positions by symbol (preserve direction by signing qty: long > 0, short < 0).
        var venueBySymbol = venuePositions
            .GroupBy(v => v.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.ToList(),
                StringComparer.OrdinalIgnoreCase);

        var expectedBySymbol = expected
            .GroupBy(e => e.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.ToList(),
                StringComparer.OrdinalIgnoreCase);

        var allSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in venueBySymbol.Keys) allSymbols.Add(k);
        foreach (var k in expectedBySymbol.Keys) allSymbols.Add(k);

        var drifts = new List<PositionDrift>();
        var orphans = new List<ExchangePositionSnapshot>();
        var phantoms = new List<ExpectedPosition>();

        foreach (var sym in allSymbols)
        {
            var venueList = venueBySymbol.TryGetValue(sym, out var v) ? v : new List<ExchangePositionSnapshot>();
            var expectedList = expectedBySymbol.TryGetValue(sym, out var e) ? e : new List<ExpectedPosition>();

            // Aggregate signed qty per side. (Hedge mode allows long+short on same symbol.)
            decimal venueLong = venueList.Where(x => x.IsLong).Sum(x => x.Quantity);
            decimal venueShort = venueList.Where(x => !x.IsLong).Sum(x => x.Quantity);
            decimal expectedLong = expectedList.Where(x => x.IsLong).Sum(x => x.Quantity);
            decimal expectedShort = expectedList.Where(x => !x.IsLong).Sum(x => x.Quantity);

            CompareSide(sym, IsLong: true, venueLong, expectedLong, drifts, orphans, phantoms,
                venueList.FirstOrDefault(x => x.IsLong),
                expectedList.FirstOrDefault(x => x.IsLong));
            CompareSide(sym, IsLong: false, venueShort, expectedShort, drifts, orphans, phantoms,
                venueList.FirstOrDefault(x => !x.IsLong),
                expectedList.FirstOrDefault(x => !x.IsLong));
        }

        bool inSync = drifts.Count == 0 && orphans.Count == 0 && phantoms.Count == 0;
        string reason = inSync
            ? $"States in sync ({allSymbols.Count} symbols, tolerance {_tolerance:P2})."
            : $"Reconciliation FAILED: {drifts.Count} drift, {orphans.Count} orphan, {phantoms.Count} phantom. Halt new entries until manually cleared.";

        if (!inSync)
        {
            _logger.LogCritical(
                "[HALT] {Reason} | Drifts: {Drifts} | Orphans: {Orphans} | Phantoms: {Phantoms}",
                reason,
                string.Join(",", drifts.Select(d => $"{d.Symbol}{(d.IsLong ? "L" : "S")}")),
                string.Join(",", orphans.Select(o => o.Symbol)),
                string.Join(",", phantoms.Select(p => p.Symbol)));
        }
        else
        {
            _logger.LogInformation("[RECO] {Reason}", reason);
        }

        return new ReconciliationResult(inSync, reason, drifts, orphans, phantoms);
    }

    private void CompareSide(
        string symbol, bool IsLong, decimal venueQty, decimal expectedQty,
        List<PositionDrift> drifts, List<ExchangePositionSnapshot> orphans, List<ExpectedPosition> phantoms,
        ExchangePositionSnapshot? venueSnap, ExpectedPosition? expectedSnap)
    {
        if (venueQty == 0m && expectedQty == 0m) return;

        if (venueQty > 0m && expectedQty == 0m)
        {
            // Position in venue we don't track. Orphan.
            if (venueSnap is not null) orphans.Add(venueSnap);
            return;
        }
        if (venueQty == 0m && expectedQty > 0m)
        {
            // Expected position absent at venue. Phantom.
            if (expectedSnap is not null) phantoms.Add(expectedSnap);
            return;
        }

        decimal denom = Math.Max(venueQty, expectedQty);
        decimal driftPct = denom > 0 ? Math.Abs(venueQty - expectedQty) / denom : 0m;
        if (driftPct > _tolerance)
        {
            drifts.Add(new PositionDrift(symbol, IsLong, expectedQty, venueQty, driftPct));
        }
    }

    public sealed record ExpectedPosition(string Symbol, decimal Quantity, bool IsLong);
    public sealed record PositionDrift(string Symbol, bool IsLong, decimal ExpectedQty, decimal VenueQty, decimal DriftPct);
    public sealed record ReconciliationResult(
        bool InSync,
        string Reason,
        IReadOnlyList<PositionDrift> Drifts,
        IReadOnlyList<ExchangePositionSnapshot> Orphans,
        IReadOnlyList<ExpectedPosition> Phantoms);
}
