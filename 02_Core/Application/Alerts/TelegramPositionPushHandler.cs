using System.Net.Http;
using System.Net.Http.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Notifications;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Alerts;

/// <summary>
/// Pushes a Telegram message every time a position opens or closes so the
/// operator can passively watch overnight without polling /positions.
/// Disabled silently when Telegram credentials are missing.
/// </summary>
public sealed class TelegramPositionPushHandler :
    INotificationHandler<PositionOpenedNotification>,
    INotificationHandler<PositionClosedNotification>
{
    private readonly ILogger<TelegramPositionPushHandler> _logger;
    private readonly IOptions<TelegramOptions> _options;
    private readonly HttpClient _http;

    public TelegramPositionPushHandler(
        ILogger<TelegramPositionPushHandler> logger,
        IOptions<TelegramOptions> options,
        IHttpClientFactory httpFactory)
    {
        _logger = logger;
        _options = options;
        _http = httpFactory.CreateClient();
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task Handle(PositionOpenedNotification note, CancellationToken ct)
    {
        var msg =
            $"🟢 OPENED {note.Side} {note.Symbol}\n" +
            $"Entry: {note.EntryPrice:F6}\n" +
            $"Qty:   {note.Quantity:F6}\n" +
            $"SL:    {note.StopLoss:F6}\n" +
            $"TP:    {note.TakeProfit:F6}\n" +
            $"Strat: {note.StrategyTag} · {note.RegimeTag}\n" +
            $"Conf:  {note.Confidence:F0}/100\n" +
            (string.IsNullOrWhiteSpace(note.Reasoning) ? "" : $"\n{Truncate(note.Reasoning, 220)}");
        await SendAsync(msg, ct).ConfigureAwait(false);
    }

    public async Task Handle(PositionClosedNotification note, CancellationToken ct)
    {
        var emoji = note.NetPnl >= 0m ? "✅" : "❌";
        var msg =
            $"{emoji} CLOSED {note.Side} {note.Symbol}\n" +
            $"PnL:   {note.NetPnl:+0.00;-0.00;0.00} ({note.NetPnlPercent:+0.00;-0.00;0.00}%)\n" +
            $"Entry→Exit: {note.EntryPrice:F6} → {note.ExitPrice:F6}\n" +
            $"Reason: {note.ExitReason}\n" +
            $"Strat:  {note.StrategyTag} · {note.RegimeTag}\n" +
            $"Held:   {(note.ExitTimeUtc - note.EntryTimeUtc).TotalMinutes:F0} min";
        await SendAsync(msg, ct).ConfigureAwait(false);
    }

    // Telegram bot API ~1 msg/sec to same chat. Burst sends (e.g. 6 trade opens in 1 sec)
    // get 429-throttled and dropped. Global semaphore + min-spacing keeps us under the limit.
    private static readonly System.Threading.SemaphoreSlim _sendGate = new(1, 1);
    private static DateTime _lastSendUtc = DateTime.MinValue;
    private static readonly TimeSpan _minSpacing = TimeSpan.FromMilliseconds(350);

    private async Task SendAsync(string text, CancellationToken ct)
    {
        var opts = _options.Value;
        if (string.IsNullOrWhiteSpace(opts.BotToken) || string.IsNullOrWhiteSpace(opts.ChatId))
        {
            _logger.LogDebug("Telegram push skipped: BotToken or ChatId not configured.");
            return;
        }

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var elapsedSinceLast = DateTime.UtcNow - _lastSendUtc;
            if (elapsedSinceLast < _minSpacing)
            {
                try { await Task.Delay(_minSpacing - elapsedSinceLast, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }

            var payload = new { chat_id = opts.ChatId.Trim(), text };
            var resp = await _http.PostAsJsonAsync(
                $"https://api.telegram.org/bot{opts.BotToken.Trim()}/sendMessage",
                payload, ct).ConfigureAwait(false);
            _lastSendUtc = DateTime.UtcNow;
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogWarning("Telegram push failed: {Status} {Body}", resp.StatusCode, Truncate(body, 160));
                if ((int)resp.StatusCode == 429) _lastSendUtc = DateTime.UtcNow.AddSeconds(2);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram push threw.");
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
