using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Bot;

public enum AlertSeverity { Info, Warning, Critical }

/// <summary>
/// Sends a real-time Telegram message when other services detect critical events
/// (worker crash signals, OKX disconnects, hybrid order failures, reconciliation drift,
/// daily-loss circuit-breaker trips). Rate-limited per event-key (default 5min cooldown)
/// to prevent spam during repeated failures.
/// </summary>
public interface IRealtimeAlerter
{
    /// <summary>Send an alert. The (key, severity) pair determines the cooldown bucket — repeated
    /// alerts with the same key are suppressed for <see cref="TimeSpan"/> per <c>Alerts:CooldownSeconds</c>.</summary>
    Task SendAsync(string key, AlertSeverity severity, string title, string body, CancellationToken ct = default);
}

public sealed class TelegramRealtimeAlerter : IRealtimeAlerter
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    // Telegram bot API allows ~30 msg/sec across DIFFERENT chats but only ~1 msg/sec to the
    // SAME chat. Burst sends (e.g. 6 trades opening simultaneously) get HTTP 429-throttled
    // and silently dropped. This semaphore + minimum-spacing pair guarantees our outbound
    // rate stays under Telegram's limit.
    private static readonly SemaphoreSlim _sendGate = new(1, 1);
    private static DateTime _lastSendUtc = DateTime.MinValue;
    private static readonly TimeSpan _minSpacing = TimeSpan.FromMilliseconds(350);

    private readonly ILogger<TelegramRealtimeAlerter> _logger;
    private readonly string _botToken;
    private readonly string _chatId;
    private readonly TimeSpan _cooldown;
    private readonly ConcurrentDictionary<string, DateTime> _lastSentUtc = new();

    public TelegramRealtimeAlerter(
        ILogger<TelegramRealtimeAlerter> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _botToken = configuration["Telegram:BotToken"] ?? string.Empty;
        _chatId = configuration["Telegram:ChatId"] ?? string.Empty;
        var cooldownSec = int.TryParse(configuration["Alerts:CooldownSeconds"], out var c) && c > 0 ? c : 300;
        _cooldown = TimeSpan.FromSeconds(cooldownSec);
    }

    public async Task SendAsync(string key, AlertSeverity severity, string title, string body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(_chatId))
        {
            _logger.LogDebug("Realtime alert skipped (no Telegram creds): {Key} {Severity} {Title}", key, severity, title);
            return;
        }

        // Rate limit: bucket by (key + severity).
        var bucket = $"{key}|{severity}";
        var now = DateTime.UtcNow;
        if (_lastSentUtc.TryGetValue(bucket, out var last) && now - last < _cooldown)
        {
            _logger.LogDebug("Realtime alert suppressed (cooldown): {Bucket} (last={Last:O})", bucket, last);
            return;
        }
        _lastSentUtc[bucket] = now;

        var icon = severity switch
        {
            AlertSeverity.Critical => "🚨",
            AlertSeverity.Warning => "⚠️",
            _ => "ℹ️"
        };

        var sb = new StringBuilder();
        sb.AppendLine($"{icon} *{severity.ToString().ToUpper()}* — {title}");
        sb.AppendLine($"_{now:yyyy-MM-dd HH:mm:ss} UTC_");
        sb.AppendLine();
        sb.AppendLine(body);

        // Acquire global send gate + enforce min spacing to stay under Telegram rate limits.
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var elapsedSinceLast = DateTime.UtcNow - _lastSendUtc;
            if (elapsedSinceLast < _minSpacing)
            {
                var wait = _minSpacing - elapsedSinceLast;
                try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }

            var payload = new { chat_id = _chatId, text = sb.ToString(), parse_mode = "Markdown" };
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"https://api.telegram.org/bot{_botToken}/sendMessage", content, ct).ConfigureAwait(false);
            _lastSendUtc = DateTime.UtcNow;
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogWarning("Realtime Telegram alert send failed: {Status} {Err}", resp.StatusCode, err);
                // 429 → respect Retry-After by extending lastSend so next call waits longer.
                if ((int)resp.StatusCode == 429)
                {
                    _lastSendUtc = DateTime.UtcNow.AddSeconds(2);
                }
            }
            else
            {
                _logger.LogInformation("Realtime alert sent: [{Severity}] {Title}", severity, title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Realtime alert send threw.");
        }
        finally
        {
            _sendGate.Release();
        }
    }
}
