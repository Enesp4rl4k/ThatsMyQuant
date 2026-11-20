using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Keeps one exchange-side catastrophe stop per open position (RESULTS_stops_take_profit_2026_11_06,
/// 15% from the average entry, mark-price trigger, closes the whole position) and the configured isolated
/// leverage on every held instrument. Stops that fired are recorded in <see cref="EdgeLabStopBlock"/> so the
/// strategies do not buy the position straight back. Runs every minute; never stops the host.
/// On a paper venue (OKX demo) the stop is run bot-side instead: demo prices drift from the real market and a stop
/// resting on the demo engine fired on wicks that never happened (ARB, 2026-11-20). The stop level comes from the
/// position's live-market entry (<see cref="LiveMarkEntryBook"/>), the trigger from the real mark price's 1-minute
/// range since the last tick, and the close is a market order on the venue. Live trading keeps the venue-side stop,
/// which also protects while the bot is down.
/// </summary>
public sealed class CatastropheStopHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly ICatastropheStopVenue _venue;
    private readonly IExchangePositionReader _positions;
    private readonly EdgeLabStopBlock _block;
    private readonly LiveMarkEntryBook? _liveEntries;
    private readonly decimal _stopPct;
    private readonly ILogger<CatastropheStopHostedService> _logger;

    public CatastropheStopHostedService(ICatastropheStopVenue venue, IExchangePositionReader positions, EdgeLabStopBlock block,
        OrderExecutionOptions options, ILogger<CatastropheStopHostedService> logger, LiveMarkEntryBook? liveEntries = null)
    {
        _venue = venue;
        _positions = positions;
        _block = block;
        _liveEntries = liveEntries;
        _stopPct = options.CatastropheStopPct;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_stopPct <= 0m)
        {
            _logger.LogInformation("Catastrophe stops disabled (OrderExecution:CatastropheStopPct = 0).");
            return;
        }
        _logger.LogInformation(_venue.IsPaperVenue && _liveEntries is not null
                ? "Catastrophe stops on (paper venue: bot-side, real-market mark): {Pct:P0} from the live-market entry."
                : "Catastrophe stops on: {Pct:P0} from average entry, mark-price trigger.", _stopPct);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await TickAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Catastrophe stop tick failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_venue.IsPaperVenue && _liveEntries is not null)
        {
            await PaperTickAsync(_liveEntries, ct).ConfigureAwait(false);
            return;
        }
        // 1. stops that fired since the last check -> block re-entry on that side
        var since = _block.LastCheckUtc.AddMinutes(-2);
        var now = DateTime.UtcNow;
        foreach (var fired in await _venue.GetTriggeredBotStopsAsync(since, ct).ConfigureAwait(false))
        {
            if (_block.IsBlocked(fired.Symbol)) continue;
            _block.Block(fired.Symbol, fired.ClosesLong ? 1 : -1);
            _logger.LogWarning("Catastrophe stop FIRED on {Symbol} ({Side}); re-entry on that side blocked until the signal changes.",
                fired.Symbol, fired.ClosesLong ? "long" : "short");
        }
        _block.MarkChecked(now);

        // 2. isolated margin back to notional / leverage, so liquidation stays beyond the stop
        await _venue.TopUpIsolatedMarginAsync(ct).ConfigureAwait(false);

        // 3. one correct stop per open position, leverage on every held instrument
        var (ok, positions) = await _positions.TryGetOpenPositionsAsync(ct).ConfigureAwait(false);
        if (!ok) return;                                            // never act on a failed read
        var stops = await _venue.GetBotStopOrdersAsync(ct).ConfigureAwait(false);
        var desired = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in positions.Where(p => p.Quantity > 0m && p.AvgEntryPrice is > 0m))
        {
            await _venue.EnsureLeverageAsync(p.Symbol, ct).ConfigureAwait(false);
            var tick = await _venue.GetTickSizeAsync(p.Symbol, ct).ConfigureAwait(false);
            desired[p.Symbol] = CatastropheStopPlanner.DesiredTrigger(p.AvgEntryPrice!.Value, p.IsLong, _stopPct, tick);
        }
        var plan = CatastropheStopPlanner.Reconcile(positions, stops, desired);
        foreach (var s in plan.Cancel)
            await _venue.CancelStopAsync(s.Symbol, s.AlgoId, ct).ConfigureAwait(false);
        foreach (var (sym, isLong, trig) in plan.Place)
        {
            if (await _venue.PlaceCatastropheStopAsync(sym, isLong, trig, ct).ConfigureAwait(false))
                _logger.LogInformation("Catastrophe stop set: {Symbol} {Side} trigger {Trigger} (mark).", sym, isLong ? "long" : "short", trig);
        }
    }

    private async Task PaperTickAsync(LiveMarkEntryBook entries, CancellationToken ct)
    {
        await _venue.TopUpIsolatedMarginAsync(ct).ConfigureAwait(false);
        foreach (var f in await PaperCatastropheStop.RunAsync(_venue, _positions, _block, entries, _stopPct, DateTime.UtcNow, ct).ConfigureAwait(false))
            _logger.LogWarning("Catastrophe stop FIRED on {Symbol} ({Side}, bot-side on the real mark: live entry {Entry}, range {Low}-{High}); " +
                               "re-entry on that side blocked until the signal changes.",
                f.Symbol, f.WasLong ? "long" : "short", f.LiveEntry, f.Low, f.High);
    }
}
