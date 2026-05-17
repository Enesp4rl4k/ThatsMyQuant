using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Bot;

/// <summary>
/// [2026-05-14 unattended hardening] Hard kill-switch: independent fail-safe that
/// force-flattens all positions when drawdown breaches a hard cap. Different from
/// <see cref="DrawdownLadderGate"/> which only BLOCKS new entries — this CLOSES
/// existing positions when things go really wrong.
///
/// Two triggers, either fires the kill:
///   1. Account drawdown ≥ -8% of basis (peak-to-current)
///   2. Daily PnL ≤ -3% of basis (intraday cliff)
///
/// On trigger:
///   - Insert ManualCloseRequest row with __FLATTEN_ALL__ sentinel (existing pipeline
///     in ManualCloseRequestProcessor handles the actual close).
///   - Set persistent kill flag (kill_switch_state.json) so worker restart honors it.
///   - Send Critical Telegram alert.
///   - Refuse to un-kill until the operator manually resets via /kill-reset command
///     OR deletes the state file. This is intentional — psychology guard.
///
/// Polls every 30s. Cheap (in-memory portfolio read). Safe to fail (does nothing if
/// portfolio uninitialized).
/// </summary>
public sealed class KillSwitchHostedService : BackgroundService
{
    private readonly ILogger<KillSwitchHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PaperPortfolio _portfolio;
    private readonly IRealtimeAlerter? _alerter;
    private readonly string _stateFile;

    private readonly decimal _drawdownTriggerPct;       // default 0.08 = -8%
    private readonly decimal _dailyPnlTriggerPct;       // default 0.03 = -3%
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(30);

    private decimal _peakEquity;
    private bool _killed;
    private string _killReason = string.Empty;
    private DateTime _killedUtc = DateTime.MinValue;

    // [2026-05-14 boot grace] First 90s after process start: SKIP evaluation entirely.
    // Reason: ExchangeBalanceSync runs at boot and may re-anchor Cash from $4999 (stale
    // snapshot) to $2000 (config cap). During that window, basis (InitialBalance) is
    // stale at $5000 while Cash is fresh at $2000 → looks like -60% drawdown → false kill.
    // After 90s the sync has settled and any persisted InitialBalance vs Cash mismatch
    // has been logged by BasisSanityCheck.
    private readonly DateTime _bootUtc = DateTime.UtcNow;
    private static readonly TimeSpan BootGrace = TimeSpan.FromSeconds(90);

    public KillSwitchHostedService(
        ILogger<KillSwitchHostedService> logger,
        IServiceProvider serviceProvider,
        IConfiguration cfg,
        PaperPortfolio portfolio,
        IRealtimeAlerter? alerter = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _portfolio = portfolio;
        _alerter = alerter;

        _drawdownTriggerPct = decimal.TryParse(
            cfg["Risk:KillSwitch:DrawdownPct"],
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var dd) && dd > 0 ? dd : 0.08m;
        _dailyPnlTriggerPct = decimal.TryParse(
            cfg["Risk:KillSwitch:DailyPnlPct"],
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var dp) && dp > 0 ? dp : 0.03m;

        var stateDir = Environment.ExpandEnvironmentVariables(
            cfg["StateDirectory"] ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThatsMyQuant"));
        _stateFile = Path.Combine(stateDir, "kill_switch_state.json");

        TryLoadState();
    }

    /// <summary>True when kill is active. Public so executors can check before opening entries.</summary>
    public bool IsKilled => _killed;

    /// <summary>Human-readable kill reason for logs / alerts.</summary>
    public string KillReason => _killReason;

    public DateTime KilledUtc => _killedUtc;

    /// <summary>Operator-only escape hatch (called via Telegram /kill-reset command).</summary>
    public void ManualReset(string operatorTag)
    {
        var prev = _killReason;
        _killed = false;
        _killReason = string.Empty;
        _killedUtc = DateTime.MinValue;
        TryPersistState();
        _logger.LogWarning("KillSwitch MANUAL RESET by {Op}. Was: {Prev}", operatorTag, prev);
    }

    private void TryLoadState()
    {
        try
        {
            if (!File.Exists(_stateFile)) return;
            var json = File.ReadAllText(_stateFile);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            _killed = root.TryGetProperty("Killed", out var k) && k.GetBoolean();
            _killReason = root.TryGetProperty("Reason", out var r) ? (r.GetString() ?? "") : "";
            if (root.TryGetProperty("KilledUtc", out var t) && t.ValueKind == JsonValueKind.String
                && DateTime.TryParse(t.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
                _killedUtc = ts;
            if (root.TryGetProperty("PeakEquity", out var pe) && pe.TryGetDecimal(out var p))
                _peakEquity = p;

            if (_killed)
            {
                _logger.LogCritical(
                    "KillSwitch state RESTORED from disk: KILLED at {Ts:o} reason={Reason}. New entries refused; run /kill-reset to clear.",
                    _killedUtc, _killReason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KillSwitch state load failed (treating as fresh).");
        }
    }

    private void TryPersistState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            var json = JsonSerializer.Serialize(new
            {
                Killed = _killed,
                Reason = _killReason,
                KilledUtc = _killedUtc.ToString("o"),
                PeakEquity = _peakEquity,
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_stateFile, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KillSwitch state persist failed.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogWarning(
            "KillSwitchHostedService starting. Triggers: drawdown ≥ -{DD:P1} OR daily PnL ≤ -{DP:P1}. Poll every {Sec}s. State={State}.",
            _drawdownTriggerPct, _dailyPnlTriggerPct, _pollInterval.TotalSeconds, _killed ? "KILLED" : "ARMED");

        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            do
            {
                try
                {
                    await EvaluateAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "KillSwitch evaluation tick failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task EvaluateAsync(CancellationToken ct)
    {
        // [2026-05-14] Boot grace: skip first 90s to let ExchangeBalanceSync stabilize.
        // Without this, a stale InitialBalance ($5000) vs sync-reset Cash ($2000) flashes
        // as -60% drawdown → false kill at the very first 30s tick. After 90s either
        // sync has run OR the basis mismatch is real and BasisSanityCheck has alerted.
        if (DateTime.UtcNow - _bootUtc < BootGrace) return;
        if (_killed) return;                 // already killed; nothing more to do

        decimal basis, equity, cash, dailyPnl;
        int openCount;
        lock (_portfolio)
        {
            basis = _portfolio.InitialBalance;
            equity = _portfolio.TotalEquity;
            cash = _portfolio.Cash;
            dailyPnl = _portfolio.DailyPnL;
            openCount = _portfolio.OpenPositions.Count;
        }

        if (basis <= 0m) return;            // not initialized yet

        // [2026-05-14] Sanity guard: if basis disagrees with cash by >2× and there are
        // no open positions, the portfolio.json is carrying a pre-config-pivot snapshot
        // that ExchangeBalanceSync hasn't re-anchored. Skip evaluation rather than fire
        // a false kill on a config-drift artifact. BasisSanityCheck already logs this.
        if (openCount == 0 && cash > 0m && basis > 0m)
        {
            var basisToCashRatio = basis / cash;
            if (basisToCashRatio > 2m || basisToCashRatio < 0.5m)
            {
                _logger.LogDebug(
                    "KillSwitch: skipping eval — basis ${Basis:N2} vs cash ${Cash:N2} ratio {Ratio:F2}× outside [0.5, 2.0]; sync not yet stable.",
                    basis, cash, basisToCashRatio);
                return;
            }
        }

        // [2026-05-17 BUG FIX — false-positive KILL on stale peak]
        // Persisted peak from a previous run can be anchored to an old basis (e.g., basis
        // was $5000 last week, now config-pivoted to $2000). On restart, _peakEquity loads
        // as $4999 while current equity is $2034 → false 59% drawdown → false KILL.
        // Detection: peak > 1.5× current basis = stale (no legitimate strategy would have
        // grown equity 50% above starting basis). Re-anchor peak to current equity.
        if (_peakEquity > basis * 1.5m && basis > 0m)
        {
            _logger.LogWarning(
                "KillSwitch: persisted peak ${OldPeak:N2} > 1.5× basis ${Basis:N2} (stale from prior basis); re-anchoring peak to current equity ${Eq:N2}.",
                _peakEquity, basis, equity);
            _peakEquity = equity;
            TryPersistState();
        }

        // Track peak. Initialize from current equity (NOT from basis) so a sync-reset
        // doesn't show as drawdown. Drawdown is high-water-mark minus current.
        if (_peakEquity == 0m) _peakEquity = equity;
        if (equity > _peakEquity)
        {
            _peakEquity = equity;
            TryPersistState();
        }

        if (_peakEquity <= 0m) return;       // can't compute drawdown

        var drawdownPct = (_peakEquity - equity) / _peakEquity;   // peak-relative
        var dailyPnlPct = basis > 0m ? -dailyPnl / basis : 0m;       // positive when in loss

        string? trigger = null;
        if (drawdownPct >= _drawdownTriggerPct)
            trigger = $"DRAWDOWN -{drawdownPct:P2} ≥ trigger -{_drawdownTriggerPct:P1} (peak ${_peakEquity:N2} → equity ${equity:N2})";
        else if (dailyPnlPct >= _dailyPnlTriggerPct)
            trigger = $"DAILY_PNL -${-dailyPnl:N2} ({-dailyPnl / basis:P2}) ≥ trigger -{_dailyPnlTriggerPct:P1}";

        if (trigger is null) return;

        // FIRE THE KILL.
        _killed = true;
        _killReason = trigger;
        _killedUtc = DateTime.UtcNow;
        TryPersistState();

        _logger.LogCritical(
            "🚨 KILL-SWITCH FIRED 🚨 reason={Reason} | basis=${Basis:N2} equity=${Eq:N2} peak=${Peak:N2} dailyPnL=${DPnL:N2} open={Open}",
            trigger, basis, equity, _peakEquity, dailyPnl, openCount);

        // Insert __FLATTEN_ALL__ ManualCloseRequest. The existing ManualCloseRequestProcessor
        // (3s poll) will pick it up and call PaperTradingService.FlattenAllPositionsAsync.
        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO "ManualCloseRequests" ("Symbol", "Side", "Reason", "Status", "RequestedUtc")
                VALUES ('__FLATTEN_ALL__', NULL, $reason, 'Pending', $now);
                """;
            var pReason = cmd.CreateParameter(); pReason.ParameterName = "$reason"; pReason.Value = $"KillSwitch: {trigger}".Substring(0, Math.Min(200, $"KillSwitch: {trigger}".Length)); cmd.Parameters.Add(pReason);
            var pNow = cmd.CreateParameter(); pNow.ParameterName = "$now"; pNow.Value = DateTime.UtcNow.ToString("o"); cmd.Parameters.Add(pNow);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _logger.LogCritical("KillSwitch: __FLATTEN_ALL__ ManualCloseRequest inserted. Processor will close all open positions within ~3s.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "KillSwitch FAILED to insert FLATTEN_ALL request — operator must manually flatten on OKX UI immediately.");
        }

        // Critical Telegram (fire-and-forget).
        if (_alerter is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _alerter.SendAsync(
                        key: "kill_switch_fired",
                        severity: AlertSeverity.Critical,
                        title: "🚨 KILL-SWITCH FIRED — all positions being flattened",
                        body:
                            $"Trigger: {trigger}\n\n" +
                            $"Basis: ${basis:N2}\n" +
                            $"Equity: ${equity:N2} (peak ${_peakEquity:N2})\n" +
                            $"Daily PnL: ${dailyPnl:N2}\n" +
                            $"Open positions: {openCount}\n\n" +
                            "All open positions are being market-closed via the ManualCloseRequest pipeline.\n" +
                            "New entries are REFUSED until /kill-reset is run.\n\n" +
                            "Verify on OKX UI: https://www.okx.com/trade-swap/btc-usdt-swap",
                        ct: CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "KillSwitch Telegram alert failed.");
                }
            });
        }
    }
}
