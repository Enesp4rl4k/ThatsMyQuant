using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Bot;

/// <summary>
/// [2026-05-17 critical fix — clock drift] OKX v5 REST endpoints reject requests
/// whose <c>OK-ACCESS-TIMESTAMP</c> deviates from server clock by &gt; 30 seconds
/// (HTTP 401, sCode 50102 "Timestamp request expired"). When Windows W32Time
/// service is disabled / mis-configured (Source: Local CMOS Clock), clock drifts
/// freely over hours and days.
///
/// Observed live: 31-second drift caused every OKX call to fail, making the bot
/// effectively blind (no position sync, no order submit, no balance fetch).
///
/// FIX: this singleton sources its time from OKX's <c>/api/v5/public/time</c>
/// endpoint at boot and every 5 minutes. <see cref="OkxExecutionService.GetIsoTimestamp"/>
/// reads <see cref="UtcNowOffsetAdjusted"/> instead of <c>DateTime.UtcNow</c> so
/// every signed request carries a timestamp anchored to OKX server clock — regardless
/// of how far local Windows clock has drifted.
///
/// Defense-in-depth:
///   • At boot, drift &gt; 5s: WARN log
///   • At boot, drift &gt; 25s: Critical Telegram alert (operator should fix W32Time)
///   • Periodic (5min): refresh offset, alert on drift trend
///   • Failure to reach OKX time endpoint: fall back to local UtcNow (degraded but trying)
/// </summary>
public sealed class OkxClockSync : BackgroundService
{
    private readonly ILogger<OkxClockSync> _logger;
    private readonly IRealtimeAlerter? _alerter;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private const string OkxTimeUrl = "https://www.okx.com/api/v5/public/time";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private const double WarnDriftSeconds = 5.0;
    private const double CriticalDriftSeconds = 25.0;

    // Static so OkxExecutionService can read without DI plumbing into every signing point.
    // Updated atomically via Interlocked.
    private static long _offsetTicks = 0;     // (OKX server UtcTicks) - (local UtcNow.Ticks)
    private static volatile bool _initialized;
    private static long _lastDriftMicroseconds;     // double can't be volatile in C#; store as long-microseconds for atomic read/write via Interlocked

    public OkxClockSync(ILogger<OkxClockSync> logger, IRealtimeAlerter? alerter = null)
    {
        _logger = logger;
        _alerter = alerter;
    }

    /// <summary>
    /// UTC clock anchored to OKX server time. Falls back to local UtcNow when not yet
    /// initialized OR last OKX fetch failed. Callers should prefer this over
    /// <see cref="DateTime.UtcNow"/> for any OKX-signed request.
    /// </summary>
    public static DateTime UtcNowOffsetAdjusted
    {
        get
        {
            var ticks = Interlocked.Read(ref _offsetTicks);
            return DateTime.UtcNow.AddTicks(ticks);
        }
    }

    public static double LastDriftSeconds => Interlocked.Read(ref _lastDriftMicroseconds) / 1_000_000.0;
    public static bool Initialized => _initialized;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogWarning(
            "OkxClockSync starting. Will fetch OKX server time every {Min}min. Drift thresholds: warn>{W}s, critical>{C}s.",
            RefreshInterval.TotalMinutes, WarnDriftSeconds, CriticalDriftSeconds);

        // Initial sync ASAP — without it, the very first OKX REST call may fail with 50102.
        await TrySyncAsync(isBoot: true, stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            do
            {
                try { await TrySyncAsync(isBoot: false, stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "OkxClockSync tick failed."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task TrySyncAsync(bool isBoot, CancellationToken ct)
    {
        DateTime localBeforeUtc, localAfterUtc, okxUtc;
        try
        {
            localBeforeUtc = DateTime.UtcNow;
            var response = await _http.GetStringAsync(OkxTimeUrl, ct).ConfigureAwait(false);
            localAfterUtc = DateTime.UtcNow;
            using var doc = JsonDocument.Parse(response);
            var ts = doc.RootElement.GetProperty("data")[0].GetProperty("ts").GetString();
            if (!long.TryParse(ts, out var okxMs))
            {
                _logger.LogWarning("OkxClockSync: could not parse OKX timestamp '{Ts}'.", ts);
                return;
            }
            okxUtc = DateTimeOffset.FromUnixTimeMilliseconds(okxMs).UtcDateTime;
        }
        catch (OperationCanceledException) { throw; }  // honor shutdown promptly, don't log "fetch failed"
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OkxClockSync: OKX time fetch failed; keeping previous offset.");
            return;
        }

        // Compensate for HTTP round-trip: assume OKX timestamp was generated at the midpoint
        // of (sent, received). For typical < 500ms RTT this corrects drift estimate by < 250ms.
        var localMidUtc = localBeforeUtc.AddTicks((localAfterUtc - localBeforeUtc).Ticks / 2);
        var driftSeconds = (localMidUtc - okxUtc).TotalSeconds;   // positive = local AHEAD of OKX
        Interlocked.Exchange(ref _lastDriftMicroseconds, (long)(driftSeconds * 1_000_000));

        // offset = OKX - local, so adding to local gives OKX-equivalent time.
        var offsetTicks = (okxUtc - localMidUtc).Ticks;
        Interlocked.Exchange(ref _offsetTicks, offsetTicks);
        _initialized = true;

        var absDrift = Math.Abs(driftSeconds);
        var driftSign = driftSeconds >= 0 ? "AHEAD" : "BEHIND";
        if (absDrift > CriticalDriftSeconds)
        {
            _logger.LogError(
                "OkxClockSync CRITICAL drift: local clock is {Sec:F2}s {Sign} OKX server. Applied offset = {OffsetMs}ms. Fix Windows W32Time service.",
                absDrift, driftSign, offsetTicks / TimeSpan.TicksPerMillisecond);

            if (_alerter is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _alerter.SendAsync(
                            key: "clock_drift_critical",
                            severity: AlertSeverity.Critical,
                            title: $"⏰ Clock drift CRITICAL: local {absDrift:F0}s {driftSign} OKX",
                            body:
                                $"Local Windows clock is {absDrift:F2}s {driftSign} OKX server time.\n" +
                                $"OKX rejects requests with drift > 30s (sCode 50102).\n" +
                                $"Bot is now applying a software offset to every signed request — calls should succeed.\n\n" +
                                $"Permanent fix needed: enable Windows Time service\n" +
                                $"  net time /set\n" +
                                $"  w32tm /resync /force\n" +
                                $"  sc config w32time start= auto && net start w32time\n\n" +
                                (isBoot ? "(Detected at boot)" : "(Detected during periodic sync)"),
                            ct: CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { }
                });
            }
        }
        else if (absDrift > WarnDriftSeconds)
        {
            _logger.LogWarning(
                "OkxClockSync drift: local clock is {Sec:F2}s {Sign} OKX server. Applied offset = {OffsetMs}ms.",
                absDrift, driftSign, offsetTicks / TimeSpan.TicksPerMillisecond);
        }
        else
        {
            _logger.LogInformation(
                "OkxClockSync OK: drift={Sec:+0.00;-0.00;0}s (within ±{Tol}s). Offset = {OffsetMs}ms.",
                driftSeconds, WarnDriftSeconds, offsetTicks / TimeSpan.TicksPerMillisecond);
        }
    }
}
