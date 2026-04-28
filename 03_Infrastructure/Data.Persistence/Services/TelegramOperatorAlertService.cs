using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy.ProMode;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Telegram bot operator alert. Reads bot token + chat ID from configuration
/// (<c>OperatorAlerts:Telegram:BotToken</c>, <c>OperatorAlerts:Telegram:ChatId</c>).
/// Falls back to log-only if either is missing.
///
/// Token/chatId setup (one-time):
///   1. Message @BotFather on Telegram → create bot → copy token
///   2. Message your bot once, then GET https://api.telegram.org/bot{TOKEN}/getUpdates
///      → find the chat.id in the response
///   3. appsettings.json:
///      "OperatorAlerts": { "Telegram": { "BotToken": "...", "ChatId": "..." } }
///
/// Register in Infrastructure DI to replace LogOnlyOperatorAlertService:
///   services.AddHttpClient();
///   services.AddSingleton&lt;IOperatorAlertService, TelegramOperatorAlertService&gt;();
/// </summary>
public sealed class TelegramOperatorAlertService : IOperatorAlertService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TelegramOperatorAlertService> _logger;
    private readonly string? _botToken;
    private readonly string? _chatId;

    public TelegramOperatorAlertService(
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        ILogger<TelegramOperatorAlertService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _botToken = configuration["OperatorAlerts:Telegram:BotToken"];
        _chatId = configuration["OperatorAlerts:Telegram:ChatId"];
    }

    public async Task NotifyAsync(AlertPriority priority, string title, string body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(_chatId))
        {
            _logger.LogWarning("Telegram alert NOT CONFIGURED — falling back to log. Title={Title}", title);
            return;
        }

        var icon = priority switch
        {
            AlertPriority.Critical => "🛑",
            AlertPriority.Warning => "⚠️",
            _ => "ℹ️"
        };
        var text = $"{icon} *{priority}* · {title}\n\n{body}\n\n_{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC_";

        try
        {
            var client = _httpFactory.CreateClient();
            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("chat_id", _chatId!),
                new KeyValuePair<string, string>("text", text),
                new KeyValuePair<string, string>("parse_mode", "Markdown")
            });
            using var resp = await client.PostAsync(url, form, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("Telegram alert HTTP {Code}", (int)resp.StatusCode);
        }
        catch (Exception ex) { _logger.LogError(ex, "Telegram alert send failed"); }
    }
}
