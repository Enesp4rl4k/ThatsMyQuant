using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Bot;

/// <summary>
/// [Sprint 6 — 2026-05-10] Per-strategy realized-Sharpe kill switch evaluator.
/// Implements the TMQ side of the Quantlab integration contract:
///   - Hourly: query closed Trade rows grouped by StrategyTag (last 30 cycles).
///   - Compute realized per-cycle return (closed PnL ÷ entry notional) per trade.
///   - Annualise per-strategy Sharpe using Quantlab's <c>expected_cyc_per_yr</c> hint.
///   - Compare against thresholds carried in the latest signal payload.
///   - Persist a per-strategy multiplier {1.0, 0.5, 0.0} to JSON state file.
///
/// EdgeLabSingleAssetHostedService reads <c>EdgeLabKillSwitchState</c> on every
/// cycle and applies the multiplier to its configured Weight. State file makes
/// the decision visible across services + restart-stable + operator-readable.
///
/// Threshold + cyc_per_yr per strategy: persisted from the latest signal doc on
/// every successful read (a small in-memory cache of the most-recent metadata
/// per strategy keyed by strategy name). Removes the need to plumb the full
/// signal-doc into the kill switch separately.
/// </summary>
public sealed class EdgeLabKillSwitchHostedService : BackgroundService
{
    public static readonly TimeSpan EvaluationInterval = TimeSpan.FromHours(1);

    private readonly ILogger<EdgeLabKillSwitchHostedService> _logger;
    private readonly IServiceProvider _services;
    private readonly EdgeLabKillSwitchState _state;
    private readonly IOperatorAlertPublisher? _alerts;

    // Cache of per-strategy contract thresholds, populated from signals as we read them.
    // Keyed by strategy name. Survives only in-process; rehydrated as new signals arrive.
    private readonly Dictionary<string, ContractThresholds> _thresholdsByStrategy = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _thresholdsLock = new();

    public EdgeLabKillSwitchHostedService(
        ILogger<EdgeLabKillSwitchHostedService> logger,
        IServiceProvider services,
        EdgeLabKillSwitchState state,
        IOperatorAlertPublisher? alerts = null)
    {
        _logger = logger;
        _services = services;
        _state = state;
        _alerts = alerts;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "EdgeLabKillSwitch starting. Interval={Interval}min, state={Path}",
            EvaluationInterval.TotalMinutes, _state.FilePath);

        // Hydrate thresholds from latest signal docs on disk so we don't wait for
        // the first hosted-service cycle to populate. Best-effort.
        TryHydrateThresholdsFromSignalsDir();

        using var timer = new PeriodicTimer(EvaluationInterval);
        try
        {
            // Run once on boot, then every interval.
            try { await EvaluateAsync(stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "EdgeLabKillSwitch initial evaluation failed"); }

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await EvaluateAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "EdgeLabKillSwitch periodic evaluation failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Public so it's exercisable in unit tests / on-demand alerts.</summary>
    public async Task EvaluateAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetService<QuantDbContext>();
        if (db is null) return;

        var since = DateTime.UtcNow.AddDays(-180);

        // Pull closed fills with strategy tag + complete entry/exit prices.
        var rows = await db.Trades
            .AsNoTracking()
            .Where(t => t.EntryTime >= since
                        && t.ExitPrice > 0m
                        && t.EntryPrice > 0m
                        && t.Quantity > 0m
                        && t.LifecycleStatus == TradeLifecycleStatus.Closed
                        && !string.IsNullOrEmpty(t.StrategyTag))
            .OrderBy(t => t.EntryTime)
            .Select(t => new
            {
                t.StrategyTag,
                t.Side,
                t.EntryPrice,
                t.ExitPrice,
                t.Quantity,
                t.TotalFeesPaid,
                t.NetFunding,
                t.EntryTime
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Per-strategy realized per-cycle returns = (gross - fees + funding) / notional.
        var byStrategy = rows.GroupBy(r => r.StrategyTag).ToDictionary(g => g.Key,
            g => g.Select(r =>
            {
                var sideMul = r.Side == TradeSide.Buy ? 1m : -1m;
                var gross = sideMul * (r.ExitPrice - r.EntryPrice) * r.Quantity;
                var net = gross - r.TotalFeesPaid + r.NetFunding;
                var notional = r.EntryPrice * r.Quantity;
                return notional > 0m ? net / notional : 0m;
            }).ToList());

        var doc = _state.Read();
        var newStates = new Dictionary<string, EdgeLabKillSwitchState.StrategyState>(StringComparer.OrdinalIgnoreCase);
        ContractThresholds defaultThresholds;
        lock (_thresholdsLock)
        {
            // Defaults from the contract when a strategy has no signal-doc yet.
            defaultThresholds = new ContractThresholds(CycPerYr: 30.0, Soft: -0.3, Hard: -1.0);
        }

        foreach (var (strategy, returns) in byStrategy)
        {
            ContractThresholds thresholds;
            lock (_thresholdsLock)
            {
                thresholds = _thresholdsByStrategy.TryGetValue(strategy, out var t) ? t : defaultThresholds;
            }

            var decision = EdgeLabKillSwitch.Resolve(
                returns, thresholds.CycPerYr, thresholds.Soft, thresholds.Hard);

            // Detect transitions: alert when multiplier changes vs prior state.
            if (doc.Strategies.TryGetValue(strategy, out var prev) &&
                prev.Multiplier != decision.Multiplier)
            {
                _logger.LogWarning(
                    "EdgeLabKillSwitch[{Strategy}]: multiplier {Prev} → {Next} (realized Sharpe {Sharpe:F2}, n={N}). Reason: {Reason}",
                    strategy, prev.Multiplier, decision.Multiplier,
                    decision.RealizedSharpeAnn, decision.NCycles, decision.Reason);
                if (_alerts is not null)
                {
                    try
                    {
                        var sev = decision.Multiplier == EdgeLabKillSwitch.HardKillMultiplier ? "critical" :
                                  decision.Multiplier == EdgeLabKillSwitch.HalfKellyMultiplier ? "warning" : "info";
                        await _alerts.PublishAsync(sev, $"KillSwitch[{strategy}]",
                            $"KillSwitch transition {prev.Multiplier:F1}× → {decision.Multiplier:F1}×",
                            $"Realized Sharpe {decision.RealizedSharpeAnn:F2} (n={decision.NCycles}). {decision.Reason}",
                            ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "KillSwitch alert publish failed"); }
                }
            }

            newStates[strategy] = new EdgeLabKillSwitchState.StrategyState(
                Strategy: strategy,
                Multiplier: decision.Multiplier,
                RealizedSharpeAnn: decision.RealizedSharpeAnn,
                NCycles: decision.NCycles,
                Reason: decision.Reason,
                UpdatedUtc: DateTime.UtcNow);
        }

        _state.Write(new EdgeLabKillSwitchState.StateDocument(DateTime.UtcNow, newStates));
        _logger.LogInformation(
            "EdgeLabKillSwitch evaluated {N} strategies. Hard-kills={Hard} Half-Kelly={Half} FullKelly={Full}",
            newStates.Count,
            newStates.Count(s => s.Value.Multiplier == EdgeLabKillSwitch.HardKillMultiplier),
            newStates.Count(s => s.Value.Multiplier == EdgeLabKillSwitch.HalfKellyMultiplier),
            newStates.Count(s => s.Value.Multiplier == EdgeLabKillSwitch.FullKellyMultiplier));
    }

    /// <summary>
    /// Update per-strategy thresholds from a fresh signal payload — called by
    /// <see cref="EdgeLabSingleAssetHostedService"/> whenever it reads a new
    /// signal so the kill switch always uses the latest contract values.
    /// </summary>
    public void UpdateThresholds(string strategy,
        double? cycPerYr, double? softThreshold, double? hardThreshold)
    {
        if (string.IsNullOrWhiteSpace(strategy)) return;
        lock (_thresholdsLock)
        {
            var existing = _thresholdsByStrategy.TryGetValue(strategy, out var t)
                ? t
                : new ContractThresholds(CycPerYr: 30.0, Soft: -0.3, Hard: -1.0);
            _thresholdsByStrategy[strategy] = new ContractThresholds(
                CycPerYr: cycPerYr ?? existing.CycPerYr,
                Soft: softThreshold ?? existing.Soft,
                Hard: hardThreshold ?? existing.Hard);
        }
    }

    private void TryHydrateThresholdsFromSignalsDir()
    {
        // Best-effort: scan signals/<strategy>/latest.json under the configured EdgeLab
        // input dir and seed _thresholdsByStrategy. If the dir isn't accessible, skip.
        try
        {
            using var scope = _services.CreateScope();
            var cfg = scope.ServiceProvider.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
            var inputDir = cfg?["EdgeLabSingleAsset:InputDir"];
            if (string.IsNullOrWhiteSpace(inputDir) || !Directory.Exists(inputDir)) return;
            foreach (var sub in Directory.EnumerateDirectories(inputDir))
            {
                var latest = Path.Combine(sub, "latest.json");
                if (!File.Exists(latest)) continue;
                try
                {
                    var json = File.ReadAllText(latest);
                    var doc = System.Text.Json.JsonSerializer.Deserialize<EdgeLabSignalDocument>(json);
                    if (doc?.Strategy is null) continue;
                    UpdateThresholds(doc.Strategy,
                        doc.Metadata?.ExpectedCycPerYr,
                        doc.Metadata?.KillSwitchRealizedSharpe30c,
                        doc.Metadata?.KillSwitchRealizedSharpeHard);
                }
                catch { /* skip malformed */ }
            }
            lock (_thresholdsLock)
            {
                _logger.LogInformation(
                    "EdgeLabKillSwitch: hydrated thresholds for {N} strategies from signals dir",
                    _thresholdsByStrategy.Count);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "EdgeLabKillSwitch threshold hydration failed"); }
    }

    private sealed record ContractThresholds(double CycPerYr, double Soft, double Hard);
}
