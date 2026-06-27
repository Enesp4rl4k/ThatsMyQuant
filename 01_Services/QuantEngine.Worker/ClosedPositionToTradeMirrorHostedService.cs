using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Bot;

/// <summary>
/// [2026-06-12 ledger drift fix — the "proper fix" promised in
/// <see cref="DbToPortfolioSyncHostedService"/>'s header note as Sprint-Post-Vacation #1]
///
/// PROBLEM: EdgeLab / hybrid / OKX-mirror closes all land in
/// <see cref="PaperPortfolio.ClosedPositions"/> but NOTHING in the live worker path
/// writes a <see cref="Trade"/> row to <c>db.Trades</c>. The EF Trades table held only
/// 8 stale rows (last 2026-04-30) while the portfolio carried 131 closed positions.
/// The two canonical consumers that read <c>db.Trades</c> were therefore BLIND:
///   - <see cref="EdgeLabKillSwitchHostedService"/> evaluated 0 strategies → every
///     live strategy stuck at the default 1.0× multiplier (kill switch inert).
///   - <see cref="PerStrategyDigestHostedService"/> emitted <c>strategies: []</c> every
///     day even when trades had closed inside its 7-day window.
///
/// SOLUTION (this service): every 5 min, snapshot <c>ClosedPositions</c> and persist a
/// <see cref="Trade"/> row per closed position into <c>db.Trades</c>, so the existing
/// consumers work unchanged. The PnL math in those consumers is price-based
/// (sideMul·(exit−entry)·qty − fees + funding); we map <c>CurrentPrice → ExitPrice</c>,
/// <c>RealFeePaid → TotalFeesPaid</c>, <c>NetFunding → NetFunding</c> so their recomputed
/// net matches the position's realized net.
///
/// FORWARD-ONLY (operator decision 2026-06-12): only positions that CLOSE at or after a
/// persisted <see cref="_cutoffUtc"/> are mirrored. This keeps the kill switch's realized
/// sample aligned with the running pre-registered tests (N≥30 plan) instead of suddenly
/// ingesting up to 180d of mixed history and demoting live strategies mid-test. The
/// cutoff is written once on first boot and is restart-stable.
///
/// IDEMPOTENT: the Trade <see cref="Trade.Id"/> is a deterministic hash of
/// (strategy, symbol, entry-ticks, exit-ticks, quantity). Re-running — or the 5-min
/// LIVE_OKX rebuild in <see cref="DbToPortfolioSyncHostedService"/> handing us a freshly
/// constructed object for the same close — never double-inserts.
/// </summary>
public sealed class ClosedPositionToTradeMirrorHostedService : BackgroundService
{
    private static readonly TimeSpan MirrorInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<ClosedPositionToTradeMirrorHostedService> _logger;
    private readonly IServiceProvider _services;
    private readonly PaperPortfolio _portfolio;
    private readonly string _stateFilePath;

    private DateTime _cutoffUtc;

    public ClosedPositionToTradeMirrorHostedService(
        ILogger<ClosedPositionToTradeMirrorHostedService> logger,
        IServiceProvider services,
        PaperPortfolio portfolio)
    {
        _logger = logger;
        _services = services;
        _portfolio = portfolio;
        _stateFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThatsMyQuant", "closed_trade_mirror_state.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Fail loud if the live-mode sentinel drifts apart from the worker's source of truth.
        // The mapper mirrors this constant (Application can't reference the worker exe).
        System.Diagnostics.Debug.Assert(
            ClosedPositionTradeMapper.LiveExecutionMarker == DbToPortfolioSyncHostedService.OkxMirrorMarker,
            "LiveExecutionMarker drifted from DbToPortfolioSyncHostedService.OkxMirrorMarker");

        _cutoffUtc = LoadOrInitCutoff();
        _logger.LogWarning(
            "ClosedPositionToTradeMirror starting. Forward-only cutoff={Cutoff:o}. Mirror ClosedPositions → db.Trades every {Min}min.",
            _cutoffUtc, MirrorInterval.TotalMinutes);

        // 40s boot grace so DbToPortfolioSync has populated the LIVE_OKX closed subset first.
        try { await Task.Delay(TimeSpan.FromSeconds(40), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(MirrorInterval);
        try
        {
            do
            {
                try { await MirrorOnceAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "ClosedPositionToTradeMirror tick failed."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task MirrorOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetService<QuantDbContext>();
        if (db is null)
        {
            _logger.LogDebug("ClosedPositionToTradeMirror: QuantDbContext unavailable; skipping.");
            return;
        }

        // Snapshot the forward-only closed subset under the portfolio lock — writers
        // (hybrid executors + DbToPortfolioSync) mutate ClosedPositions concurrently.
        List<PaperPosition> closed;
        lock (_portfolio)
        {
            closed = _portfolio.ClosedPositions
                .Where(p => p.ExitTime is { } xt && xt >= _cutoffUtc
                            && p.EntryPrice > 0m && p.CurrentPrice > 0m && p.Quantity > 0m
                            && !string.IsNullOrWhiteSpace(p.StrategyTag))
                .ToList();
        }

        if (closed.Count == 0) return;

        var candidates = closed
            .GroupBy(ClosedPositionTradeMapper.MirrorId)
            .Select(g => (Id: g.Key, Pos: g.First()))
            .ToList();

        var ids = candidates.Select(c => c.Id).ToHashSet();
        var existing = await db.Trades
            .AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var existingSet = existing.ToHashSet();

        var toInsert = candidates
            .Where(c => !existingSet.Contains(c.Id))
            .Select(c => ClosedPositionTradeMapper.MapToTrade(c.Id, c.Pos))
            .ToList();

        if (toInsert.Count == 0) return;

        db.Trades.AddRange(toInsert);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "ClosedPositionToTradeMirror: inserted {N} new Trade rows (forward-only since {Cutoff:yyyy-MM-dd}). Strategies: {Strats}",
            toInsert.Count, _cutoffUtc,
            string.Join(", ", toInsert.GroupBy(t => t.StrategyTag).Select(g => $"{g.Key}×{g.Count()}")));
    }

    /// <summary>Forward-only cutoff: read the persisted value, or write "now" on first boot.
    /// Restart-stable so a relaunch never backfills pre-cutoff history.</summary>
    private DateTime LoadOrInitCutoff()
    {
        try
        {
            if (File.Exists(_stateFilePath))
            {
                var json = File.ReadAllText(_stateFilePath);
                var doc = JsonSerializer.Deserialize<CutoffState>(json);
                if (doc is { CutoffUtc: var c } && c > DateTime.MinValue)
                    return DateTime.SpecifyKind(c, DateTimeKind.Utc);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ClosedPositionToTradeMirror: cutoff read failed; re-initialising."); }

        var cutoff = DateTime.UtcNow;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
            File.WriteAllText(_stateFilePath,
                JsonSerializer.Serialize(new CutoffState { CutoffUtc = cutoff },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ClosedPositionToTradeMirror: cutoff persist failed; using in-memory cutoff."); }
        return cutoff;
    }

    private sealed class CutoffState
    {
        public DateTime CutoffUtc { get; set; }
    }
}
