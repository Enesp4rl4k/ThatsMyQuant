namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>
/// In-process and cross-process operator alert sink. Any service in any process
/// that wants the dashboard NotificationsBell to display an event calls this.
///
/// Two implementations exist:
///   • <c>MediatorOperatorAlertPublisher</c> — same-process, publishes via MediatR.
///     Used inside Listener.Api where the SignalR hub lives.
///   • <c>DbOperatorAlertPublisher</c> — cross-process, INSERTs into the
///     <c>OperatorAlertEvents</c> SQLite table. Listener.Api's
///     <c>OperatorAlertDbReaderWorker</c> polls and republishes.
///
/// Severity values: <c>info</c> | <c>success</c> | <c>warning</c> | <c>error</c>.
/// Source is a short tag (≤40 chars), e.g. <c>RiskSupervisor</c>, <c>Promotion</c>,
/// <c>Funding</c>, <c>Pairs</c>, <c>Settings</c>.
/// </summary>
public interface IOperatorAlertPublisher
{
    Task PublishAsync(string severity, string source, string title, string body, CancellationToken ct = default);
}
