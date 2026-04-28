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
    private readonly IAlertHistoryStore? _history;

    public TelegramAlertService(
        ILogger<TelegramAlertService> logger,
        HttpClient httpClient,
        IOptions<TelegramOptions> options,
        IAlertHistoryStore? history = null)
    {
        _logger = logger;
        _httpClient = httpClient;
        _options = options;
        _history = history;
    }

    public async Task SendTradeAlertAsync(string symbol, TradeDecision decision, IndicatorSnapshot snapshot, AlertAuditContext auditContext)
    {
        var opts = _options.Value;
        var title = $"Trade {decision.Side} {symbol}";
        var body = BuildMessage(symbol, decision, snapshot, auditContext);
        var isMock = string.IsNullOrEmpty(opts.BotToken);
        var entryId = 0;
        if (_history is not null)
        {
            entryId = await _history.RecordAsync(new AlertHistoryEntry
            {
                Kind = AlertKind.Trade,
                Channel = isMock ? AlertChannel.Mock : AlertChannel.Telegram,
                Status = AlertDispatchStatus.Pending,
                Symbol = symbol,
                Title = title,
                Body = body,
                Metadata = $"stage={auditContext.PromotionStage};mode={auditContext.ExecutionMode}",
            }).ConfigureAwait(false);
        }

        if (isMock)
        {
            _logger.LogInformation(
                "MOCK ALERT {Symbol} {Side} | {Edge} | {Exit} | {Stage} | {ExactBucket}",
                symbol,
                decision.Side,
                auditContext.EdgeSummary,
                auditContext.ExitSummary,
                auditContext.PromotionStage,
                auditContext.ExactBucketImpact);
            if (_history is not null && entryId > 0)
                await _history.UpdateStatusAsync(entryId, AlertDispatchStatus.Skipped, "BotToken not configured (mock)").ConfigureAwait(false);
            return;
        }

        try
        {
            var payload = new
            {
                chat_id = opts.ChatId,
                text = body
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var url = $"https://api.telegram.org/bot{opts.BotToken}/sendMessage";
            var response = await _httpClient.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to send Telegram alert: {Status}. {Error}", response.StatusCode, error);
                if (_history is not null && entryId > 0)
                    await _history.UpdateStatusAsync(entryId, AlertDispatchStatus.Failed, $"{response.StatusCode}: {error}").ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Telegram alert sent successfully for {Symbol}.", symbol);
                if (_history is not null && entryId > 0)
                    await _history.UpdateStatusAsync(entryId, AlertDispatchStatus.Sent).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while sending Telegram alert.");
            if (_history is not null && entryId > 0)
                await _history.UpdateStatusAsync(entryId, AlertDispatchStatus.Failed, ex.Message).ConfigureAwait(false);
        }
    }

    private static string BuildMessage(string symbol, TradeDecision decision, IndicatorSnapshot snapshot, AlertAuditContext auditContext)
    {
        return $@"
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
    }
}
