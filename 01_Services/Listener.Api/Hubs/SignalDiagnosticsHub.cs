using Microsoft.AspNetCore.SignalR;

namespace Listener.Api.Hubs;

/// <summary>
/// SignalR hub for the Signal Diagnostics dashboard. The server fans out the
/// full <c>SignalDiagnosticsUpdate</c> snapshot every ~3 seconds via
/// <see cref="Services.SignalDiagnosticsBroadcastService"/>. Clients only need
/// to <c>JoinSignalDiagnostics</c> to receive the stream.
/// </summary>
/// <remarks>
/// Why a dedicated hub instead of extending <see cref="MarketDataHub"/>:
/// <list type="bullet">
/// <item>Different fan-out cadence (3s for diagnostics vs 100ms for market ticks).</item>
/// <item>Different audience — operators on the Diagnostics page, not traders on Markets.</item>
/// <item>Connection-level isolation: one slow diagnostics consumer cannot back-pressure
/// the market-data path.</item>
/// </list>
/// </remarks>
public sealed class SignalDiagnosticsHub : Hub
{
    /// <summary>
    /// Idempotent join. Clients call this on connect; the server adds them to the
    /// "signal-diagnostics" group so subsequent <c>SignalDiagnosticsUpdate</c>
    /// broadcasts reach them. No payload — the next tick (within 3s) delivers the
    /// initial snapshot.
    /// </summary>
    public Task JoinSignalDiagnostics()
        => Groups.AddToGroupAsync(Context.ConnectionId, "signal-diagnostics");

    /// <summary>
    /// Idempotent leave. Use when the client navigates away from the Diagnostics page
    /// to stop receiving updates and reduce server fan-out work.
    /// </summary>
    public Task LeaveSignalDiagnostics()
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, "signal-diagnostics");
}
