using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Single-line "I'm alive" heartbeat to console + Information log every 60s.
/// Different purpose from <c>ServiceHeartbeatHostedService</c> (DB row for dashboard);
/// this one is for the operator who is staring at CMD/terminal and wants to know
/// at a glance: bot ÇALIŞIYOR mu, paper state ne, next funding settlement ne kadar?
///
/// Prefix is intentionally distinctive (<c>[HB]</c>) so a tail/grep filter is trivial.
/// Reads file-based state (paper_portfolio.json + funding_long_state.json + pairs state)
/// rather than the DB so it survives even if EF/SQLite hiccup.
///
/// 2026-05-07: Drought alarm — if open=0 AND lastClose>=48h, fire a Telegram
/// notification (cooldown 24h) so a silent bypass-mode regression is caught
/// fast. Self-contained HTTP POST to keep the heartbeat path independent of
/// the trade-alert pipeline (which requires a TradeDecision payload).
/// </summary>
public sealed class WorkerHeartbeatConsoleHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DroughtThreshold = TimeSpan.FromHours(48);
    private static readonly TimeSpan DroughtAlertCooldown = TimeSpan.FromHours(24);
    private static readonly HttpClient TelegramHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ILogger<WorkerHeartbeatConsoleHostedService> _logger;
    private readonly string _stateDir;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly string? _telegramBotToken;
    private readonly string? _telegramChatId;
    private DateTime? _lastDroughtAlertUtc;
    private readonly string _droughtStatePath;

    public WorkerHeartbeatConsoleHostedService(
        ILogger<WorkerHeartbeatConsoleHostedService> logger,
        IConfiguration cfg)
    {
        _logger = logger;
        _stateDir = Environment.ExpandEnvironmentVariables(
            cfg["StateDirectory"]
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ThatsMyQuant"));
        _telegramBotToken = cfg["Telegram:BotToken"];
        _telegramChatId = cfg["Telegram:ChatId"];

        // [2026-05-13] Persist drought-alert timestamp across worker restarts. Without this,
        // a 12d drought + 3 worker restarts in 1h = 3 redundant Telegram alerts (in-memory
        // _lastDroughtAlertUtc is null after each restart → cooldown bypassed).
        _droughtStatePath = Path.Combine(_stateDir, "drought_alert_state.json");
        TryLoadDroughtState();
    }

    private void TryLoadDroughtState()
    {
        try
        {
            if (!File.Exists(_droughtStatePath)) return;
            var json = File.ReadAllText(_droughtStatePath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("LastDroughtAlertUtc", out var el)
                && el.ValueKind == JsonValueKind.String
                && DateTime.TryParse(el.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
            {
                _lastDroughtAlertUtc = ts;
            }
        }
        catch
        {
            // non-fatal: corrupted state file → start as if never alerted
        }
    }

    private void TryPersistDroughtState()
    {
        try
        {
            Directory.CreateDirectory(_stateDir);
            var json = JsonSerializer.Serialize(new { LastDroughtAlertUtc = _lastDroughtAlertUtc?.ToString("o") });
            File.WriteAllText(_droughtStatePath, json);
        }
        catch
        {
            // non-fatal — worst case is a duplicate alert next restart
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Boot line — printed immediately so operator confirms the service started
        EmitOnce(boot: true);

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                EmitOnce(boot: false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — flush a final goodbye line so the operator sees a clean stop
            _logger.LogInformation("[HB] worker stopping (uptime={Uptime})", FormatTimespan(DateTime.UtcNow - _startedUtc));
        }
    }

    private void EmitOnce(bool boot)
    {
        try
        {
            var nowUtc = DateTime.UtcNow;
            var uptime = FormatTimespan(nowUtc - _startedUtc);
            var memMb = Math.Round(Process.GetCurrentProcess().WorkingSet64 / 1024d / 1024d, 0);

            // [2026-05-14 unattended hardening] Watchdog heartbeat file. External Watchdog
            // script polls this file's mtime; if stale (>5min) it kills + relaunches the
            // worker. File-based vs DB-based because PowerShell watchdog has no SQLite client.
            try
            {
                Directory.CreateDirectory(_stateDir);
                var hbPath = Path.Combine(_stateDir, "worker_heartbeat.txt");
                File.WriteAllText(hbPath,
                    $"{nowUtc:o}|pid={Process.GetCurrentProcess().Id}|uptime={uptime}|mem={memMb}MB");
            }
            catch
            {
                // non-fatal: watchdog will detect stale file → restart, which is the recovery
            }

            // --- portfolio snapshot (file-based, no DB) ---
            var portPath = Path.Combine(_stateDir, "paper_portfolio.json");
            int open = -1, closed = -1;
            decimal cash = -1m;
            string lastCloseAge = "n/a";
            DateTime? lastExitUtc = null;  // captured for drought check
            if (File.Exists(portPath))
            {
                try
                {
                    using var fs = File.Open(portPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var doc = JsonDocument.Parse(fs);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("Cash", out var cashEl) && cashEl.TryGetDecimal(out var c)) cash = c;
                    if (root.TryGetProperty("OpenPositions", out var op) && op.ValueKind == JsonValueKind.Array) open = op.GetArrayLength();
                    if (root.TryGetProperty("ClosedPositions", out var cp) && cp.ValueKind == JsonValueKind.Array)
                    {
                        closed = cp.GetArrayLength();
                        if (closed > 0)
                        {
                            var last = cp[closed - 1];
                            if (last.TryGetProperty("ExitTime", out var et) && et.TryGetDateTime(out var exit))
                            {
                                lastExitUtc = exit;
                                lastCloseAge = FormatTimespan(nowUtc - exit) + " ago";
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastCloseAge = $"parse-err({ex.GetType().Name})";
                }
            }

            // --- per-strategy scan freshness from state-file mtimes ---
            var fundLongAge = StateFileAge(nowUtc, "funding_long_state.json");
            var pairsLongAge = StateFileAge(nowUtc, "pairs_long_state.json");
            var pairsShortAge = StateFileAge(nowUtc, "pairs_short_state.json");

            // --- next funding settlement (00 / 08 / 16 UTC) ---
            var nextSettle = NextSettlementEta(nowUtc);

            var portStr = (open >= 0 && closed >= 0)
                ? $"open={open} closed={closed} cash=${cash:F2}"
                : "portfolio=missing";

            var prefix = boot ? "[HB BOOT]" : "[HB]";
            _logger.LogInformation(
                "{Prefix} up={Up} mem={Mem}MB | {Port} | scans: fund={Fund} pairsL={PL} pairsS={PS} | lastClose={LC} | nextSettle={NS}",
                prefix, uptime, memMb, portStr, fundLongAge, pairsLongAge, pairsShortAge, lastCloseAge, nextSettle);

            // --- drought alarm: open=0 AND lastClose >= 48h triggers Telegram (24h cooldown) ---
            if (!boot && open == 0 && lastExitUtc.HasValue)
            {
                var droughtAge = nowUtc - lastExitUtc.Value;
                if (droughtAge >= DroughtThreshold)
                {
                    var sinceLastAlert = _lastDroughtAlertUtc.HasValue
                        ? nowUtc - _lastDroughtAlertUtc.Value
                        : TimeSpan.MaxValue;
                    if (sinceLastAlert >= DroughtAlertCooldown)
                    {
                        _lastDroughtAlertUtc = nowUtc;
                        TryPersistDroughtState();   // [2026-05-13] survive worker restarts
                        _logger.LogWarning(
                            "[HB DROUGHT] no trades for {Age} (open=0, closed={Closed}). Sending Telegram alert.",
                            FormatTimespan(droughtAge), closed);
                        // Fire-and-forget — heartbeat must not block on network IO
                        _ = TrySendDroughtAlertAsync(droughtAge, fundLongAge, pairsLongAge, pairsShortAge, nextSettle, closed);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Heartbeat must NEVER crash the worker — log + move on
            _logger.LogWarning(ex, "[HB] heartbeat emit failed");
        }
    }

    private async Task TrySendDroughtAlertAsync(
        TimeSpan droughtAge, string fundLongAge, string pairsLongAge, string pairsShortAge,
        string nextSettle, int closed)
    {
        try
        {
            if (string.IsNullOrEmpty(_telegramBotToken) || string.IsNullOrEmpty(_telegramChatId))
            {
                _logger.LogInformation("[HB DROUGHT] Telegram not configured — alert skipped (logged only).");
                return;
            }
            var msg =
                $"⚠️ ThatsMyQuant DROUGHT ALARM\n" +
                $"No trades for {FormatTimespan(droughtAge)} (open=0, closed lifetime={closed})\n" +
                $"Scans: fund={fundLongAge} pairsL={pairsLongAge} pairsS={pairsShortAge}\n" +
                $"Next funding settle: {nextSettle}\n" +
                $"Worker uptime: {FormatTimespan(DateTime.UtcNow - _startedUtc)}\n" +
                $"\nCheck: (1) launcher used? (2) thresholds met by market? (3) state files updating?";
            var payload = new { chat_id = _telegramChatId, text = msg };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url = $"https://api.telegram.org/bot{_telegramBotToken}/sendMessage";
            var resp = await TelegramHttpClient.PostAsync(url, content).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
                _logger.LogInformation("[HB DROUGHT] Telegram alert sent (drought={Age}).", FormatTimespan(droughtAge));
            else
                _logger.LogWarning("[HB DROUGHT] Telegram send failed: {Status}", resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HB DROUGHT] Telegram send threw — drought alert lost.");
        }
    }

    private string StateFileAge(DateTime nowUtc, string fileName)
    {
        var path = Path.Combine(_stateDir, fileName);
        if (!File.Exists(path)) return "missing";
        var age = nowUtc - File.GetLastWriteTimeUtc(path);
        return FormatTimespan(age);
    }

    private static string FormatTimespan(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60) return $"{(int)ts.TotalSeconds}s";
        if (ts.TotalMinutes < 60) return $"{(int)ts.TotalMinutes}m";
        if (ts.TotalHours < 48) return $"{ts.TotalHours:F1}h";
        return $"{(int)ts.TotalDays}d";
    }

    private static string NextSettlementEta(DateTime nowUtc)
    {
        // OKX perpetual swap funding settles at 00:00, 08:00, 16:00 UTC
        var todayMidnight = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc);
        var candidates = new[]
        {
            todayMidnight,
            todayMidnight.AddHours(8),
            todayMidnight.AddHours(16),
            todayMidnight.AddDays(1),  // next-day 00:00 fallback
        };
        foreach (var c in candidates)
        {
            if (c > nowUtc) return FormatTimespan(c - nowUtc);
        }
        return "n/a";
    }
}
