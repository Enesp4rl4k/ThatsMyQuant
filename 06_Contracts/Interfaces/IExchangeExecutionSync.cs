using System;
using System.Threading;
using System.Threading.Tasks;

namespace ThatsMyQuant.Contracts.Interfaces;

public sealed record ExecutionReport(
    string Symbol,
    string ClientOrderId,
    string ExchangeOrderId,
    decimal FilledQuantity,
    decimal RemainingQuantity,
    decimal AveragePrice,
    decimal LastFillPrice,
    decimal LastFillQuantity,
    string Status,
    long LatencyMs,
    DateTime TimestampUtc
);

/// <summary>
/// Exchange-agnostic interface for syncing real-time execution reports (Fills/Cancels)
/// to the internal Portfolio/OMS without REST polling.
/// </summary>
public interface IExchangeExecutionSync : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Stream of incoming execution reports from the connected exchange.
    /// </summary>
    IObservable<ExecutionReport> ExecutionReports { get; }
}
