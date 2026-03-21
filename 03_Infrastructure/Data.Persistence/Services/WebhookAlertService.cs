using Microsoft.Extensions.Logging;
using System.Text.Json;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Infrastructure.Services;

public class WebhookAlertService : IAlertingService
{
    private readonly ILogger<WebhookAlertService> _logger;
    private readonly HttpClient _httpClient;
    
    // Config placeholders (Would come from AppSettings/Secrets)
    private const string WebhookUrl = "";

    public WebhookAlertService(ILogger<WebhookAlertService> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task SendTradeAlertAsync(string symbol, TradeDecision decision, IndicatorSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(WebhookUrl))
        {
            return;
        }

        try
        {
            var payload = new
            {
                Title = $"Trade Alert: {symbol}",
                Symbol = symbol,
                Side = decision.Side.ToString(),
                Confidence = decision.Confidence,
                Price = snapshot.CurrentPrice,
                StopLoss = decision.StopLoss,
                TakeProfit = decision.TakeProfit,
                Reasoning = decision.Reasoning,
                Time = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(WebhookUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to execute Webhook for {Symbol}: {Status}", symbol, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while executing Webhook alert.");
        }
    }
}
