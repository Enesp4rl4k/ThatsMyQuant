using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Infrastructure.Services;

public class TelegramAlertService : IAlertingService
{
    private readonly ILogger<TelegramAlertService> _logger;
    private readonly HttpClient _httpClient;
    private readonly IOptions<TelegramOptions> _options;

    public TelegramAlertService(
        ILogger<TelegramAlertService> logger,
        HttpClient httpClient,
        IOptions<TelegramOptions> options)
    {
        _logger = logger;
        _httpClient = httpClient;
        _options = options;
    }

    public async Task SendTradeAlertAsync(string symbol, TradeDecision decision, IndicatorSnapshot snapshot, AlertAuditContext auditContext)
    {
        var opts = _options.Value;
        if (string.IsNullOrEmpty(opts.BotToken))
        {
            _logger.LogInformation(
                "MOCK ALERT {Symbol} {Side} | {Edge} | {Exit} | {Stage} | {ExactBucket}",
                symbol,
                decision.Side,
                auditContext.EdgeSummary,
                auditContext.ExitSummary,
                auditContext.PromotionStage,
                auditContext.ExactBucketImpact);
            return;
        }

        var message = $@"
Trade Signal: {symbol}
- Action: {decision.Side}
- Confidence: {decision.Confidence}/100
- Price: {snapshot.CurrentPrice:F2}
- SL: {decision.StopLoss:F2} | TP: {decision.TakeProfit:F2}
- Mode: {auditContext.ExecutionMode}
- Stage: {auditContext.PromotionStage}
- Exact bucket: {auditContext.ExactBucketImpact}
- Exact bucket note: {auditContext.ExactBucketSummary}
- Edge: {auditContext.EdgeSummary}
- Exit: {auditContext.ExitSummary}
- Allocator: {auditContext.AllocatorImpact}
- Venue: {auditContext.VenueImpact}
- Reasoning: {auditContext.Reasoning}
";

        try
        {
            var payload = new
            {
                chat_id = opts.ChatId,
                text = message
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var url = $"https://api.telegram.org/bot{opts.BotToken}/sendMessage";
            var response = await _httpClient.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to send Telegram alert: {Status}. {Error}", response.StatusCode, error);
            }
            else
            {
                _logger.LogInformation("Telegram alert sent successfully for {Symbol}.", symbol);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while sending Telegram alert.");
        }
    }
}
