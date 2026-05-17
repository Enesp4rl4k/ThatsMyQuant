using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Bot;

/// <summary>
/// [Sprint 4 — TCA, 2026-05-10] Periodically snapshots the venue mark price at
/// {+1s, +10s, +60s, +300s} after every fresh trade fill, then writes one row per
/// horizon into <see cref="MarkoutLedger"/>. Adverse-selection bps over each
/// horizon is the canonical maker-quality measurement: high adverse selection
/// on maker fills = we're providing liquidity to better-informed counterparties.
///
/// Implementation: a single 1-second timer scans the Trade table for fills not
/// yet horizon-covered, then probes the latest order-book snapshot for each
/// pending (trade × horizon). Cheap: no per-trade scheduler, no Tasks pool —
/// the timer cycle is the schedule. With ~35 fills/month expected from the
/// 11-strategy stack post-promotion, the loop processes &lt;1 trade/minute on
/// average so the simple poll is more than enough.
///
/// Storage strategy:
///   - In-memory <see cref="_pendingByTrade"/> dict tracks "horizons not yet
///     captured for this trade" so a single fill turns into 4 ledger rows over
///     ~5 minutes without re-querying the DB.
///   - DB scan only fires every <see cref="ScanIntervalSeconds"/> to find new
///     trades; horizon ticking happens off the in-memory dict every second.
///   - On worker restart we lose any in-flight trades whose horizon hadn't
///     completed yet. Acceptable: missing 4 markout rows on restart vs. complex
///     persistence dance.
/// </summary>
public sealed class MarkoutSnapshotHostedService : BackgroundService
{
    public const int ScanIntervalSeconds = 5;
    public static readonly int[] HorizonsSeconds = new[] { 1, 10, 60, 300 };

    private readonly ILogger<MarkoutSnapshotHostedService> _logger;
    private readonly IServiceProvider _services;
    private readonly MarkoutLedger _ledger;

    private sealed record FillSnapshot(
        Guid TradeId,
        string Strategy,
        string Symbol,
        string Side,
        decimal FillPrice,
        DateTime FillUtc,
        string ExecutionSource);

    // tradeId → (snapshot, set of horizons still pending)
    private readonly Dictionary<Guid, (FillSnapshot Snap, HashSet<int> Pending)> _pendingByTrade = new();
    private DateTime _lastScanUtc = DateTime.MinValue;

    public MarkoutSnapshotHostedService(
        ILogger<MarkoutSnapshotHostedService> logger,
        IServiceProvider services,
        MarkoutLedger ledger)
    {
        _logger = logger;
        _services = services;
        _ledger = ledger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "MarkoutSnapshot starting. Horizons={Horizons}s, scan every {Scan}s, ledger={Path}",
            string.Join(',', HorizonsSeconds), ScanIntervalSeconds, _ledger.FilePath);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try { await TickAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "MarkoutSnapshot tick failed"); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Discover new fills no more often than every ScanIntervalSeconds. Cheap query
        // bounded to recent trades (last 10 minutes) so the index hits.
        if ((now - _lastScanUtc).TotalSeconds >= ScanIntervalSeconds)
        {
            await DiscoverNewFillsAsync(now, ct).ConfigureAwait(false);
            _lastScanUtc = now;
        }

        // Tick all pending (trade × horizon). Capture mark when horizon elapsed.
        var completed = new List<Guid>();
        using var scope = _services.CreateScope();
        var exchange = scope.ServiceProvider.GetService<IExchangeService>();

        foreach (var (tradeId, entry) in _pendingByTrade)
        {
            var elapsed = (now - entry.Snap.FillUtc).TotalSeconds;
            var due = entry.Pending.Where(h => elapsed >= h).ToList();
            foreach (var h in due)
            {
                var mark = TryGetMarkPrice(exchange, entry.Snap.Symbol);
                if (mark > 0m)
                {
                    var adverseBps = MarkoutLedger.ComputeAdverseBps(entry.Snap.Side, entry.Snap.FillPrice, mark);
                    var row = new MarkoutEntry(
                        FillUtc: entry.Snap.FillUtc,
                        SnapshotUtc: now,
                        HorizonSeconds: h,
                        TradeId: entry.Snap.TradeId,
                        Strategy: entry.Snap.Strategy,
                        Symbol: entry.Snap.Symbol,
                        Side: entry.Snap.Side,
                        FillPrice: entry.Snap.FillPrice,
                        MarkPrice: mark,
                        AdverseBps: adverseBps,
                        ExecutionSource: entry.Snap.ExecutionSource);
                    try { _ledger.Append(row); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Markout ledger append failed"); }
                }
                else
                {
                    _logger.LogDebug(
                        "MarkoutSnapshot: no mark for {Symbol} at +{Horizon}s — skipping",
                        entry.Snap.Symbol, h);
                }
                entry.Pending.Remove(h);
            }
            if (entry.Pending.Count == 0) completed.Add(tradeId);
        }
        foreach (var done in completed) _pendingByTrade.Remove(done);

        await Task.CompletedTask;
    }

    private async Task DiscoverNewFillsAsync(DateTime nowUtc, CancellationToken ct)
    {
        // Cap window to "since last scan minus longest horizon" so a fill that
        // happened during a brief downtime window still gets captured (best-effort;
        // missed horizons are harmless because elapsed-check happens on each tick).
        var since = nowUtc.AddSeconds(-(ScanIntervalSeconds + HorizonsSeconds[^1] + 60));

        using var scope = _services.CreateScope();
        QuantDbContext? db = null;
        try { db = scope.ServiceProvider.GetService<QuantDbContext>(); }
        catch { /* graceful when DB not registered for some envs */ }
        if (db is null) return;

        // Pull recent fills with a known fill price; skip ones we already track.
        // EF query keeps the projection narrow — no need for the 80-column SELECT.
        List<FillSnapshot> rows;
        try
        {
            var ids = _pendingByTrade.Keys.ToHashSet();
            // [2026-05-13] Was Take(100) without OrderBy → EF Core warned + SQLite returned
            // rows in undefined order, so this could pick any arbitrary 100 fills (potentially
            // missing the most-recent ones markout cares about). OrderBy SubmittedTime
            // descending ensures the freshest fills are always sampled.
            rows = await db.Trades
                .AsNoTracking()
                .Where(t => t.SubmittedTime >= since && t.EntryPrice > 0m && t.FilledQuantity > 0m)
                .OrderByDescending(t => t.SubmittedTime ?? t.EntryTime)
                .Select(t => new FillSnapshot(
                    t.Id,
                    t.StrategyTag,
                    t.Symbol,
                    t.Side.ToString(),
                    t.EntryPrice,
                    t.SubmittedTime ?? t.EntryTime,
                    t.ExecutionSource))
                .Take(100)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            rows = rows.Where(r => !ids.Contains(r.TradeId)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MarkoutSnapshot fill-scan query failed");
            return;
        }

        foreach (var r in rows)
        {
            _pendingByTrade[r.TradeId] = (r, new HashSet<int>(HorizonsSeconds));
        }
        if (rows.Count > 0)
        {
            _logger.LogInformation(
                "MarkoutSnapshot: discovered {Count} new fill(s) for markout tracking (pending dict size={Size})",
                rows.Count, _pendingByTrade.Count);
        }
    }

    private static decimal TryGetMarkPrice(IExchangeService? exchange, string symbol)
    {
        if (exchange is null) return 0m;
        if (exchange.TryGetLatestOrderBook(symbol, out var book) &&
            book.Bids is { Length: > 0 } && book.Asks is { Length: > 0 })
        {
            var bid = book.Bids[0].Price;
            var ask = book.Asks[0].Price;
            if (bid > 0m && ask > 0m) return (bid + ask) / 2m;
        }
        return 0m;
    }
}
