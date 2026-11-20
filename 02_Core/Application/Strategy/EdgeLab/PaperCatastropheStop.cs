using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.Strategy.EdgeLab;

/// <summary>
/// Bot-side catastrophe stop for paper venues (OKX demo), one pass. The venue's own stops are cancelled (they trigger on
/// the demo engine's prices); each open position's stop sits <c>stopPct</c> from its live-market entry
/// (<see cref="LiveMarkEntryBook"/>) and fires when the real mark price's 1-minute range since the previous pass reaches it.
/// A fired stop closes the whole position at market on the venue and blocks re-entry on that side
/// (<see cref="EdgeLabStopBlock"/>), as the venue-side stop does in live trading.
/// </summary>
public static class PaperCatastropheStop
{
    /// <summary>Lookback cap: the live mark candle request covers the last 10 one-minute candles.</summary>
    public static readonly TimeSpan MaxLookback = TimeSpan.FromMinutes(9);

    public sealed record Fired(string Symbol, bool WasLong, decimal LiveEntry, decimal Low, decimal High);

    public static async Task<IReadOnlyList<Fired>> RunAsync(ICatastropheStopVenue venue, IExchangePositionReader positions,
        EdgeLabStopBlock block, LiveMarkEntryBook entries, decimal stopPct, DateTime nowUtc, CancellationToken ct = default)
    {
        var fired = new List<Fired>();
        var (ok, open) = await positions.TryGetOpenPositionsAsync(ct).ConfigureAwait(false);
        if (!ok) return fired;                                      // never act on a failed read

        foreach (var s in await venue.GetBotStopOrdersAsync(ct).ConfigureAwait(false))
            await venue.CancelStopAsync(s.Symbol, s.AlgoId, ct).ConfigureAwait(false);

        var held = open.Where(p => p.Quantity > 0m).ToList();
        entries.Retain(held.Select(p => p.Symbol));
        var since = block.LastCheckUtc;
        var from = since < nowUtc - MaxLookback ? nowUtc - MaxLookback : since;
        foreach (var p in held)
        {
            await venue.EnsureLeverageAsync(p.Symbol, ct).ConfigureAwait(false);
            var range = await venue.GetLiveMarkRangeAsync(p.Symbol, from, ct).ConfigureAwait(false);
            if (range is null) continue;
            var (lo, hi) = range.Value;
            var entry = entries.Observe(p.Symbol, p.IsLong ? p.Quantity : -p.Quantity, (lo + hi) / 2m);
            if (entry is null || !LiveMarkEntryBook.Triggered(p.IsLong, entry.Value, stopPct, lo, hi)) continue;
            if (!await venue.ClosePositionAtMarketAsync(p.Symbol, p.IsLong, ct).ConfigureAwait(false)) continue;
            entries.Forget(p.Symbol);
            block.Block(p.Symbol, p.IsLong ? 1 : -1);
            fired.Add(new Fired(p.Symbol, p.IsLong, entry.Value, lo, hi));
        }
        block.MarkChecked(nowUtc);
        return fired;
    }
}
