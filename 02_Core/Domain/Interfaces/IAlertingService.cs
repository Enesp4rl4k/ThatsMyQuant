using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Domain.Interfaces;

public interface IAlertingService
{
    /// <summary>
    /// Sends a real-time notification to a configured endpoint (e.g., Telegram, Webhook)
    /// when a trade signal is approved.
    /// </summary>
    Task SendTradeAlertAsync(string symbol, TradeDecision decision, IndicatorSnapshot snapshot, AlertAuditContext auditContext);
}
