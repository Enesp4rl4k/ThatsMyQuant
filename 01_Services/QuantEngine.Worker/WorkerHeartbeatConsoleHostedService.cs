using System.Diagnostics;
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
/// </summary>
public sealed class WorkerHeartbeatConsoleHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly ILogger<WorkerHeartbeatConsoleHostedService> _logger;
    private readonly string _stateDir;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

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

            // --- portfolio snapshot (file-based, no DB) ---
            var portPath = Path.Combine(_stateDir, "paper_portfolio.json");
            int open = -1, closed = -1;
            decimal cash = -1m;
            string lastCloseAge = "n/a";
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
        }
        catch (Exception ex)
        {
            // Heartbeat must NEVER crash the worker — log + move on
            _logger.LogWarning(ex, "[HB] heartbeat emit failed");
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
