using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Bot;

/// <summary>
/// 2026-05-07 (Sprint 1.2 — Master Plan v3) — demo→live execution drift early-warning.
///
/// Premortem #4 (Demo→Live Microstructure Drift) identified that fill quality
/// silently degrades when the bot moves from OKX-demo (synthetic matching, generous
/// queue) to live (real queue position competition). Maker fill ratio is the
/// canonical leading indicator: if post-only orders drop below ~75% maker fill,
/// the queue race is being lost and realized fees double from the assumed model.
///
/// Mechanism:
///   1. Every <c>MakerFillRatio:IntervalMinutes</c> (default 60), pulls last
///      <c>LookbackDays</c> (default 30) of fills via OKX
///      <c>/api/v5/trade/fills-history</c>.
///   2. Reads the per-fill <c>execType</c> field ("T"=taker, "M"=maker).
///   3. Computes <c>maker / (maker+taker)</c> as the maker fill ratio.
///   4. Persists per-symbol + aggregate to
///      <c>%LOCALAPPDATA%\ThatsMyQuant\maker_fill_ratio.json</c>.
///   5. Alerts via Telegram when aggregate ratio drops below
///      <c>AlertThreshold</c> (default 0.75).
///
/// Read-only — never blocks trades. Drift is information; the operator decides
/// whether to halt promotion or recalibrate the cost model.
///
/// Why poll vs continuous capture: OKX fills-history is the canonical source of
/// truth (matches venue books). Continuous capture during entry/close path can
/// miss fills if the worker restarts mid-fill, so we pull from the historical
/// endpoint every cycle. Cheap (one paged HTTP per cycle, fills come back
/// already filtered by time window).
/// </summary>
public sealed class MakerFillRatioMonitorHostedService : BackgroundService
{
    private readonly ILogger<MakerFillRatioMonitorHostedService> _logger;
    private readonly IExchangePositionReader? _fillsReader;
    private readonly IRealtimeAlerter? _alerter;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly int _lookbackDays;
    private readonly decimal _alertThreshold;
    private readonly string _stateFilePath;

    public MakerFillRatioMonitorHostedService(
        ILogger<MakerFillRatioMonitorHostedService> logger,
        IConfiguration cfg,
        IExchangePositionReader? fillsReader = null,
        IRealtimeAlerter? alerter = null)
    {
        _logger = logger;
        _fillsReader = fillsReader;
        _alerter = alerter;
        _enabled = !bool.TryParse(cfg["MakerFillRatio:Enabled"], out var en) || en;
        var minutes = int.TryParse(cfg["MakerFillRatio:IntervalMinutes"], out var m) && m > 0 ? m : 60;
        _interval = TimeSpan.FromMinutes(minutes);
        _lookbackDays = int.TryParse(cfg["MakerFillRatio:LookbackDays"], out var d) && d > 0 ? d : 30;
        _alertThreshold = decimal.TryParse(
            cfg["MakerFillRatio:AlertThreshold"],
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var t) && t > 0 ? t : 0.75m;

        var stateDir = Environment.ExpandEnvironmentVariables(
            cfg["StateDirectory"]
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ThatsMyQuant"));
        _stateFilePath = Path.Combine(stateDir, "maker_fill_ratio.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("MakerFillRatioMonitor disabled (MakerFillRatio:Enabled=false).");
            return;
        }
        if (_fillsReader is null)
        {
            _logger.LogWarning("MakerFillRatioMonitor: no IExchangePositionReader registered (Mock mode?). Service inert.");
            return;
        }

        _logger.LogWarning(
            "MakerFillRatioMonitor starting. Interval={I}m Lookback={D}d AlertThreshold={T:F2} (ratio < threshold = queue race lost, fees ~2x assumed)",
            _interval.TotalMinutes, _lookbackDays, _alertThreshold);

        // Initial delay 2 minutes — let the bot finish boot + cred resolution.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                try { await RunAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "MakerFillRatio tick failed."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var until = DateTime.UtcNow;
        var since = until.AddDays(-_lookbackDays);

        IReadOnlyList<ExchangeFillSnapshot> fills;
        try
        {
            fills = await _fillsReader!.GetFillsHistoryAsync(since, until, "SWAP", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MakerFillRatio: GetFillsHistoryAsync failed.");
            return;
        }

        if (fills.Count == 0)
        {
            _logger.LogInformation("MAKER_FILL_RATIO no fills in last {D}d (drought / new account).", _lookbackDays);
            // Persist a "no data" state so the dashboard knows the service ran.
            await PersistAsync(new
            {
                ComputedAtUtc = DateTime.UtcNow,
                LookbackDays = _lookbackDays,
                Threshold = _alertThreshold,
                TotalFills = 0,
                MakerFills = 0,
                TakerFills = 0,
                UnknownExecType = 0,
                AggregateRatio = (decimal?)null,
                EntryFills = 0,
                EntryMakerFills = 0,
                EntryTakerFills = 0,
                EntryRatio = (decimal?)null,
                MarketExitFills = 0,
                Symbols = Array.Empty<object>()
            }, ct).ConfigureAwait(false);
            return;
        }

        // Aggregate counts (all fills, entry+exit mixed).
        int total = fills.Count;
        int maker = fills.Count(f => string.Equals(f.ExecType, "M", StringComparison.OrdinalIgnoreCase));
        int taker = fills.Count(f => string.Equals(f.ExecType, "T", StringComparison.OrdinalIgnoreCase));
        int unknown = total - maker - taker;
        decimal? aggregate = (maker + taker) > 0 ? (decimal)maker / (maker + taker) : (decimal?)null;

        // 2026-05-07 (Sprint 1.2 live-fill validation): split entry vs exit.
        // FundingHybridExecutor + ExecuteTradeHandler route ENTRIES through limit/post_only
        // (when MakerEntryEnabled, default true) but always close via Market for guaranteed
        // exit semantics. Mixing market exits into the maker-ratio drowns the metric in
        // structurally-taker fills and fires false QUEUE-RACE-LOST alarms. Premortem #4
        // measures the entry-side queue race specifically: how often does the post-only
        // limit at the touch get filled vs walked over.
        //
        // OKX `/api/v5/trade/fills-history` does NOT return `ordType`. Two entry/exit signals
        // are available depending on position-side mode:
        //   • HEDGE Mode: subType = "3"=open long, "4"=open short, "5"=close long, "6"=close short.
        //   • NET Mode (our config): subType = "1"=buy, "2"=sell — direction only, no open/close
        //     distinction. Use RealizedPnl (OKX `fillPnl`): 0 ⇒ entry (no close booked),
        //     non-zero ⇒ exit (close fill realized PnL). Fee accounting is separate from fillPnl
        //     so a flat-PnL exit is theoretically possible but vanishingly rare with real
        //     volatility — false-classify rate is in the noise.
        //
        // Order of precedence: prefer subType when it's a Hedge Mode value (3/4/5/6) so
        // the classifier survives a future config flip; otherwise fall back to fillPnl.
        bool IsEntry(ExchangeFillSnapshot f) =>
            f.SubType == "3" || f.SubType == "4"                                // Hedge Mode entry
            || ((f.SubType == "1" || f.SubType == "2" || f.SubType is null)     // Net Mode (or legacy)
                && f.RealizedPnl == 0m);
        bool IsExit(ExchangeFillSnapshot f) =>
            f.SubType == "5" || f.SubType == "6"                                // Hedge Mode exit
            || ((f.SubType == "1" || f.SubType == "2" || f.SubType is null)
                && f.RealizedPnl != 0m);

        var entryFills = fills.Where(IsEntry).ToList();
        int marketExitFills = fills.Count(IsExit);
        int entryMaker = entryFills.Count(f => string.Equals(f.ExecType, "M", StringComparison.OrdinalIgnoreCase));
        int entryTaker = entryFills.Count(f => string.Equals(f.ExecType, "T", StringComparison.OrdinalIgnoreCase));
        decimal? entryRatio = (entryMaker + entryTaker) > 0
            ? (decimal)entryMaker / (entryMaker + entryTaker)
            : (decimal?)null;

        // Per-symbol breakdown — entry-only metric (market exits filtered out).
        var perSymbol = fills
            .GroupBy(f => f.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                int sm = g.Count(x => string.Equals(x.ExecType, "M", StringComparison.OrdinalIgnoreCase));
                int st = g.Count(x => string.Equals(x.ExecType, "T", StringComparison.OrdinalIgnoreCase));
                int su = g.Count() - sm - st;
                decimal? sr = (sm + st) > 0 ? (decimal)sm / (sm + st) : (decimal?)null;
                int sEntryM = g.Count(x => IsEntry(x) && string.Equals(x.ExecType, "M", StringComparison.OrdinalIgnoreCase));
                int sEntryT = g.Count(x => IsEntry(x) && string.Equals(x.ExecType, "T", StringComparison.OrdinalIgnoreCase));
                int sMktExit = g.Count(IsExit);
                decimal? sEntryR = (sEntryM + sEntryT) > 0 ? (decimal)sEntryM / (sEntryM + sEntryT) : (decimal?)null;
                return new
                {
                    Symbol = g.Key,
                    TotalFills = g.Count(),
                    MakerFills = sm,
                    TakerFills = st,
                    UnknownExecType = su,
                    Ratio = sr,
                    EntryFills = sEntryM + sEntryT,
                    EntryMakerFills = sEntryM,
                    EntryTakerFills = sEntryT,
                    MarketExitFills = sMktExit,
                    EntryRatio = sEntryR,
                    BelowThreshold = sEntryR is not null && sEntryR < _alertThreshold
                };
            })
            .OrderByDescending(x => x.TotalFills)
            .ToList();

        // Log summary — both metrics. Entry ratio is the actionable Premortem #4 detector.
        var verdict = entryRatio is null
            ? "NO-ENTRY-FILLS-OR-LEGACY-DATA"
            : entryRatio >= _alertThreshold ? "OK" : "QUEUE-RACE-LOST";
        _logger.LogWarning(
            "MAKER_FILL_RATIO total={T}(M={M},T={Tk},U={U},aggregate={R}) entry={EN}(M={EM},T={ET},ratio={ER}) marketExits={ME} threshold={Th:F2} verdict={V}",
            total, maker, taker, unknown,
            aggregate is null ? "n/a" : aggregate.Value.ToString("F2"),
            entryFills.Count, entryMaker, entryTaker,
            entryRatio is null ? "n/a" : entryRatio.Value.ToString("F2"),
            marketExitFills, _alertThreshold, verdict);

        foreach (var s in perSymbol.Take(8))
        {
            _logger.LogInformation(
                "  MAKER_FILL[{Sym}] total={N}(M={M},T={T}) entry={EN}(M={EM},T={ET},ratio={ER}) mktExit={ME}",
                s.Symbol, s.TotalFills, s.MakerFills, s.TakerFills,
                s.EntryFills, s.EntryMakerFills, s.EntryTakerFills,
                s.EntryRatio is null ? "n/a" : s.EntryRatio.Value.ToString("F2"),
                s.MarketExitFills);
        }

        // Persist state — entry metric is the headline; aggregate kept for sanity.
        await PersistAsync(new
        {
            ComputedAtUtc = DateTime.UtcNow,
            LookbackDays = _lookbackDays,
            Threshold = _alertThreshold,
            TotalFills = total,
            MakerFills = maker,
            TakerFills = taker,
            UnknownExecType = unknown,
            AggregateRatio = aggregate,
            EntryFills = entryFills.Count,
            EntryMakerFills = entryMaker,
            EntryTakerFills = entryTaker,
            EntryRatio = entryRatio,
            MarketExitFills = marketExitFills,
            Symbols = perSymbol
        }, ct).ConfigureAwait(false);

        // Alert ONLY on entry-ratio < threshold (need ≥10 entry-classified fills for stat power).
        // Aggregate ratio with market exits mixed in is structurally biased toward zero and
        // would fire on every cycle even when post-only entries are healthy.
        if (entryRatio is not null && entryRatio < _alertThreshold && (entryMaker + entryTaker) >= 10 && _alerter is not null)
        {
            var topOffenders = perSymbol
                .Where(s => s.EntryRatio is not null && s.EntryRatio < _alertThreshold && (s.EntryMakerFills + s.EntryTakerFills) >= 3)
                .Take(5);
            var lines = topOffenders.Select(s =>
                $"  {s.Symbol}: {s.EntryRatio:F2} (M={s.EntryMakerFills} T={s.EntryTakerFills})");
            await _alerter.SendAsync(
                key: "maker_fill_ratio_below_threshold",
                severity: AlertSeverity.Warning,
                title: "⚠️ Maker fill ratio below threshold (entry-only)",
                body:
                    $"Entry-only ratio: {entryRatio:F2} (threshold {_alertThreshold:F2})\n" +
                    $"Last {_lookbackDays}d entries: M={entryMaker} T={entryTaker} ({entryFills.Count} entry fills, {marketExitFills} market exits filtered)\n\n" +
                    "Top offenders by symbol:\n" + string.Join("\n", lines) + "\n\n" +
                    "Implication: post-only entries losing the queue race. Realized fees " +
                    "trending toward 2× the assumed maker rate. Recalibrate cost model OR " +
                    "FREEZE live promotion until ratio recovers > 0.75.",
                ct: ct).ConfigureAwait(false);
        }
    }

    private async Task PersistAsync(object stateDoc, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(stateDoc, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_stateFilePath, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MakerFillRatio: state persist failed (non-fatal).");
        }
    }
}
