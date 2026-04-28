using Microsoft.Extensions.Logging;
using System.Text.Json;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

public class WebhookAlertService : IAlertingService
{
    private readonly ILogger<WebhookAlertService> _logger;
    private readonly HttpClient _httpClient;
    private readonly IAlertHistoryStore? _history;

    private const string WebhookUrl = "";

    public WebhookAlertService(ILogger<WebhookAlertService> logger, HttpClient httpClient, IAlertHistoryStore? history = null)
    {
        _logger = logger;
        _httpClient = httpClient;
        _history = history;
    }

    public async Task SendTradeAlertAsync(string symbol, TradeDecision decision, IndicatorSnapshot snapshot, AlertAuditContext auditContext)
    {
        if (string.IsNullOrEmpty(WebhookUrl))
        {
            return;
        }

        var title = $"Trade Alert: {symbol}";
        var payload = new
        {
            Title = title,
            Symbol = symbol,
            Side = decision.Side.ToString(),
            Confidence = decision.Confidence,
            Price = snapshot.CurrentPrice,
            StopLoss = decision.StopLoss,
            TakeProfit = decision.TakeProfit,
            ExecutionMode = auditContext.ExecutionMode,
            PromotionStage = auditContext.PromotionStage,
            StageExplanation = auditContext.StageExplanation,
            ExactBucketImpact = auditContext.ExactBucketImpact,
            ExactBucketSummary = auditContext.ExactBucketSummary,
            EdgeSummary = auditContext.EdgeSummary,
            ExitSummary = auditContext.ExitSummary,
            VenueImpact = auditContext.VenueImpact,
            AllocatorImpact = auditContext.AllocatorImpact,
            Reasoning = auditContext.Reasoning,
            Time = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(payload);
        var entryId = 0;
        if (_history is not null)
        {
            entryId = await _history.RecordAsync(new AlertHistoryEntry
            {
                Kind = AlertKind.Trade,
                Channel = AlertChannel.Webhook,
                Status = AlertDispatchStatus.Pending,
                Symbol = symbol,
                Title = title,
                Body = json,
                Metadata = $"stage={auditContext.PromotionStage};mode={auditContext.ExecutionMode}",
            }).ConfigureAwait(false);
        }

        try
        {
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(WebhookUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to execute Webhook for {Symbol}: {Status}", symbol, response.StatusCode);
                if (_history is not null && entryId > 0)
                    await _history.UpdateStatusAsync(entryId, AlertDispatchStatus.Failed, response.StatusCode.ToString()).ConfigureAwait(false);
            }
            else if (_history is not null && entryId > 0)
            {
                await _history.UpdateStatusAsync(entryId, AlertDispatchStatus.Sent).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while executing Webhook alert.");
            if (_history is not null && entryId > 0)
                await _history.UpdateStatusAsync(entryId, AlertDispatchStatus.Failed, ex.Message).ConfigureAwait(false);
        }
    }
}
