using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.Strategy.ProMode;

/// <summary>
/// Out-of-band operator alert channel. Critical events (circuit trip, correlation
/// panic, kill switch, large-DD warning) must reach the operator even when they're
/// asleep / not at the dashboard.
///
/// Supported backends (pluggable):
///   • Telegram bot  — primary (operator keeps phone next to bed)
///   • Discord webhook — secondary (team visibility)
///   • Email / SMS   — fallback for extreme events
///   • Log-only      — dev/paper-trade default (no external integration)
///
/// Priority levels:
///   • Info      — routine (daily summary, regime change)
///   • Warning   — attention needed (DD > 3%, correlation > 0.5)
///   • Critical  — immediate response (circuit trip, kill switch, correlation > 0.7)
/// </summary>
public interface IOperatorAlertService
{
    Task NotifyAsync(AlertPriority priority, string title, string body, CancellationToken ct = default);
}

public enum AlertPriority { Info, Warning, Critical }

/// <summary>
/// Default no-op / log-only implementation. Paper-trade safe; live deployment should
/// replace this with a real channel (<c>TelegramAlertService</c>, webhook, etc.).
/// Log output goes to the standard logger at level appropriate to priority.
/// </summary>
public sealed class LogOnlyOperatorAlertService : IOperatorAlertService
{
    private readonly ILogger<LogOnlyOperatorAlertService> _logger;
    public LogOnlyOperatorAlertService(ILogger<LogOnlyOperatorAlertService> logger) { _logger = logger; }

    public Task NotifyAsync(AlertPriority priority, string title, string body, CancellationToken ct = default)
    {
        var level = priority switch
        {
            AlertPriority.Critical => LogLevel.Error,
            AlertPriority.Warning => LogLevel.Warning,
            _ => LogLevel.Information
        };
        _logger.Log(level, "[ALERT · {Priority}] {Title} — {Body}", priority, title, body);
        return Task.CompletedTask;
    }
}

// NOTE: TelegramOperatorAlertService is implemented in the Infrastructure layer
// (03_Infrastructure/Data.Persistence) where IHttpClientFactory is available. See
// TelegramOperatorAlertService.cs over there. Application layer keeps only the
// interface + log-only fallback so core domain stays pure.
