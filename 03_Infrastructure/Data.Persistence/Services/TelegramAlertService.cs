using Microsoft.Extensions.Logging;
using System.Text.Json;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Infrastructure.Services;

public class TelegramAlertService : IAlertingService
{
    private readonly ILogger<TelegramAlertService> _logger;
    private readonly HttpClient _httpClient;
    
    // Config placeholders (Would come from AppSettings/Secrets)
    private const string BotToken = "";
    private const string ChatId = "";

    public TelegramAlertService(ILogger<TelegramAlertService> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task SendTradeAlertAsync(string symbol, TradeDecision decision, IndicatorSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(BotToken))
        {
            _logger.LogInformation("📢 [MOCK ALERT] {Symbol} {Side} | Confidence: {Confidence}/100", symbol, decision.Side, decision.Confidence);
            return;
        }

        string message = $@"
🚀 *Trade Signal: {symbol}*
- *Action*: {decision.Side}
- *Confidence*: {decision.Confidence}/100
- *Price*: {snapshot.CurrentPrice:F2}
- *SL*: {decision.StopLoss:F2} | *TP*: {decision.TakeProfit:F2}
- *Reasoning*: _{decision.Reasoning}_
";

        try
        {
            var payload = new
            {
                chat_id = ChatId,
                text = message,
                parse_mode = "Markdown"
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var url = $"https://api.telegram.org/bot{BotToken}/sendMessage";
            var response = await _httpClient.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to send Telegram alert: {Status}. {Error}", response.StatusCode, error);
            }
            else
            {
                _logger.LogInformation("✅ Telegram alert sent successfully for {Symbol}.", symbol);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while sending Telegram alert.");
        }
    }
}
