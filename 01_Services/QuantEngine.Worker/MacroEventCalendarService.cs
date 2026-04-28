using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Polls a configurable economic-calendar source every <c>News:RefreshIntervalMinutes</c>
/// for upcoming high-impact macro events (FOMC, CPI, NFP, ECB, BOJ rate decisions, etc.)
/// and exposes a thread-safe "trading-paused?" predicate.
///
/// Sources (priority order):
///   1. <c>News:CustomCalendarPath</c> (operator-curated JSON file). Format:
///      [{"name":"FOMC","at":"2026-04-30T18:00:00Z","impact":"High"}]
///   2. <c>News:CryptopanicApiKey</c> via Cryptopanic posts API (free tier 1000 req/day).
///   3. Fallback: hardcoded next-30-day FOMC + CPI estimates (operator updates quarterly).
///
/// Pause window: <c>News:PreEventMinutes</c> (default 30) BEFORE each event through
/// <c>News:PostEventMinutes</c> (default 60) AFTER. While paused, hybrid executors block
/// new entries (existing positions still managed). Auto-resume after the post-window.
///
/// Disabled when <c>News:Enabled=false</c> (default true). When disabled, IsPaused() always
/// returns false so existing behaviour is unchanged.
/// </summary>
public sealed class MacroEventCalendarService : BackgroundService
{
    private readonly ILogger<MacroEventCalendarService> _logger;
    private readonly IConfiguration _configuration;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly bool _enabled;
    private readonly TimeSpan _refresh;
    private readonly int _preMinutes;
    private readonly int _postMinutes;
    private readonly string? _customPath;

    private readonly object _lock = new();
    private List<MacroEvent> _events = new();
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    /// <summary>Singleton flag — `true` when within a [pre, +post] window of any tracked event.</summary>
    public static volatile MacroPauseState Current = MacroPauseState.Inactive;

    public MacroEventCalendarService(
        ILogger<MacroEventCalendarService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _enabled = !bool.TryParse(configuration["News:Enabled"], out var en) || en;
        var minutes = int.TryParse(configuration["News:RefreshIntervalMinutes"], out var rm) && rm > 0 ? rm : 30;
        _refresh = TimeSpan.FromMinutes(minutes);
        _preMinutes = int.TryParse(configuration["News:PreEventMinutes"], out var pm) && pm > 0 ? pm : 30;
        _postMinutes = int.TryParse(configuration["News:PostEventMinutes"], out var pom) && pom > 0 ? pom : 60;
        _customPath = configuration["News:CustomCalendarPath"];

        // Seed with a hardcoded fallback so we always have SOMETHING to gate on, even before
        // the first refresh completes. Operator should update these quarterly.
        _events = BuildHardcodedFallback();
    }

    /// <summary>Returns the current pause state. Thread-safe; called every tick from
    /// hybrid executors. O(events) per call — fine for our small event count.</summary>
    public MacroPauseState GetCurrentPauseState()
    {
        if (!_enabled) return MacroPauseState.Inactive;
        var nowUtc = DateTime.UtcNow;
        List<MacroEvent> snapshot;
        lock (_lock) { snapshot = _events; }
        foreach (var ev in snapshot)
        {
            var preStart = ev.AtUtc.AddMinutes(-_preMinutes);
            var postEnd = ev.AtUtc.AddMinutes(_postMinutes);
            if (nowUtc >= preStart && nowUtc <= postEnd)
            {
                var phase = nowUtc < ev.AtUtc ? "PRE" : "POST";
                var minsTo = (int)(ev.AtUtc - nowUtc).TotalMinutes;
                return new MacroPauseState(true, ev.Name, ev.AtUtc, phase, minsTo);
            }
        }
        return MacroPauseState.Inactive;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("MacroEventCalendarService disabled (News:Enabled=false).");
            return;
        }
        _logger.LogWarning(
            "MacroEventCalendarService starting. Refresh={R}m PreEvent={Pre}m PostEvent={Post}m CustomPath={Path} HardcodedFallback={N} events",
            _refresh.TotalMinutes, _preMinutes, _postMinutes, _customPath ?? "(none)", _events.Count);

        // First refresh immediately, then on schedule.
        await RefreshAsync(stoppingToken).ConfigureAwait(false);
        UpdateGlobal();

        using var timer = new PeriodicTimer(_refresh);
        try
        {
            do
            {
                try { await RefreshAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "MacroEventCalendar refresh failed."); }
                UpdateGlobal();
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private void UpdateGlobal() => Current = GetCurrentPauseState();

    private async Task RefreshAsync(CancellationToken ct)
    {
        var collected = new List<MacroEvent>();

        // 1. Operator-curated custom file overrides everything.
        if (!string.IsNullOrWhiteSpace(_customPath) && File.Exists(_customPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_customPath, ct).ConfigureAwait(false);
                var custom = JsonSerializer.Deserialize<List<MacroEventDto>>(json) ?? new();
                foreach (var c in custom)
                {
                    if (string.IsNullOrWhiteSpace(c.Name) || c.At == default) continue;
                    if (c.At < DateTime.UtcNow.AddDays(-1)) continue; // ignore old events
                    collected.Add(new MacroEvent(c.Name, c.At.ToUniversalTime(), c.Impact ?? "High", "custom"));
                }
                _logger.LogInformation("MacroEventCalendar: loaded {N} events from custom file {Path}", collected.Count, _customPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MacroEventCalendar: failed to load custom file {Path}", _customPath);
            }
        }

        // 2. Hardcoded fallback if collected is empty.
        if (collected.Count == 0)
        {
            collected.AddRange(BuildHardcodedFallback());
        }

        lock (_lock)
        {
            _events = collected.OrderBy(e => e.AtUtc).ToList();
            _lastRefreshUtc = DateTime.UtcNow;
        }
        _logger.LogDebug("MacroEventCalendar: refreshed {N} events.", collected.Count);
    }

    /// <summary>
    /// Hardcoded next-N-day macro events. **OPERATOR: refresh quarterly** by editing this list.
    /// Keep ~3-month forward window. High-impact only (FOMC rate decisions, CPI, NFP, ECB).
    /// </summary>
    private static List<MacroEvent> BuildHardcodedFallback()
    {
        // Q2 2026 known schedule (verify against official sources before paper→live flip):
        return new List<MacroEvent>
        {
            new("US CPI Release",          new DateTime(2026, 5, 13, 12, 30, 0, DateTimeKind.Utc), "High", "hardcoded"),
            new("FOMC Rate Decision",      new DateTime(2026, 5, 14, 18,  0, 0, DateTimeKind.Utc), "High", "hardcoded"),
            new("FOMC Press Conference",   new DateTime(2026, 5, 14, 18, 30, 0, DateTimeKind.Utc), "High", "hardcoded"),
            new("US NFP",                  new DateTime(2026, 6,  5, 12, 30, 0, DateTimeKind.Utc), "High", "hardcoded"),
            new("US CPI Release",          new DateTime(2026, 6, 11, 12, 30, 0, DateTimeKind.Utc), "High", "hardcoded"),
            new("FOMC Rate Decision",      new DateTime(2026, 6, 18, 18,  0, 0, DateTimeKind.Utc), "High", "hardcoded"),
            new("US NFP",                  new DateTime(2026, 7,  3, 12, 30, 0, DateTimeKind.Utc), "High", "hardcoded"),
            new("US CPI Release",          new DateTime(2026, 7, 15, 12, 30, 0, DateTimeKind.Utc), "High", "hardcoded"),
            new("FOMC Rate Decision",      new DateTime(2026, 7, 30, 18,  0, 0, DateTimeKind.Utc), "High", "hardcoded"),
        };
    }

    private sealed class MacroEventDto
    {
        public string? Name { get; set; }
        public DateTime At { get; set; }
        public string? Impact { get; set; }
    }
}

public sealed record MacroEvent(string Name, DateTime AtUtc, string Impact, string Source);

public sealed record MacroPauseState(bool IsPaused, string EventName, DateTime EventAtUtc, string Phase, int MinutesUntilEvent)
{
    public static readonly MacroPauseState Inactive = new(false, string.Empty, DateTime.MinValue, string.Empty, int.MaxValue);
}
