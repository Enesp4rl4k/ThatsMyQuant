using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Helpers;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Observability;

public static class TradingLogScope
{
    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    public static IDisposable Begin(ILogger logger, Signal signal)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(signal);

        var correlationId = ResolveCorrelationId(signal);
        return logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["SignalId"] = signal.Id,
            ["Symbol"] = signal.Symbol,
            ["Exchange"] = signal.Exchange
        }) ?? NoopDisposable.Instance;
    }

    public static IDisposable Begin(ILogger logger, Trade trade)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(trade);

        return logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = trade.SignalId == Guid.Empty ? trade.Id.ToString("N") : trade.SignalId.ToString("N"),
            ["SignalId"] = trade.SignalId,
            ["TradeId"] = trade.Id,
            ["Symbol"] = trade.Symbol
        }) ?? NoopDisposable.Instance;
    }

    public static string ResolveCorrelationId(Signal signal)
    {
        if (signal.Metadata.TryGetValue("CorrelationId", out var correlationId) &&
            !string.IsNullOrWhiteSpace(correlationId))
        {
            return correlationId;
        }

        var resolved = signal.Metadata.TryGetValue(ExecutionOrderIdentity.ClientOrderIdKey, out var clientOrderId) &&
                       !string.IsNullOrWhiteSpace(clientOrderId)
            ? clientOrderId
            : signal.Id.ToString("N");

        signal.Metadata["CorrelationId"] = resolved;
        return resolved;
    }
}
