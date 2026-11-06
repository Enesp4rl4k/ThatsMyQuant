using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>Pure planning for exchange-side catastrophe stops (one per open position).</summary>
public static class CatastropheStopPlanner
{
    /// <summary>Re-place a stop when it is more than 0.5% off the desired level (entry moved after a rebalance).</summary>
    public const decimal RepriceTolerance = 0.005m;

    public sealed record Plan(IReadOnlyList<VenueStopOrder> Cancel, IReadOnlyList<(string Symbol, bool IsLong, decimal Trigger)> Place);

    public static decimal DesiredTrigger(decimal avgEntry, bool isLong, decimal stopPct, decimal tick)
    {
        var raw = isLong ? avgEntry * (1m - stopPct) : avgEntry * (1m + stopPct);
        if (tick <= 0m) return raw;
        // round away from the price so the stop is never tighter than intended
        return isLong ? Math.Floor(raw / tick) * tick : Math.Ceiling(raw / tick) * tick;
    }

    public static Plan Reconcile(IReadOnlyList<ExchangePositionSnapshot> positions, IReadOnlyList<VenueStopOrder> stops,
        IReadOnlyDictionary<string, decimal> desired)
    {
        var cancel = new List<VenueStopOrder>();
        var place = new List<(string, bool, decimal)>();
        var held = positions.Where(p => p.Quantity > 0m).GroupBy(p => p.Symbol, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var s in stops.Where(s => !held.ContainsKey(s.Symbol)))
            cancel.Add(s);                                          // position gone: stale stop
        foreach (var (sym, p) in held)
        {
            if (!desired.TryGetValue(sym, out var want) || want <= 0m) continue;  // no entry price: leave as is
            var mine = stops.Where(s => string.Equals(s.Symbol, sym, StringComparison.OrdinalIgnoreCase)).ToList();
            var ok = mine.Count == 1 && mine[0].ClosesLong == p.IsLong && Math.Abs(mine[0].TriggerPx / want - 1m) <= RepriceTolerance;
            if (ok) continue;
            cancel.AddRange(mine);
            place.Add((sym, p.IsLong, want));
        }
        return new Plan(cancel, place);
    }
}
