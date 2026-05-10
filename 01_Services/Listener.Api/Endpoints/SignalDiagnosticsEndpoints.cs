using Listener.Api.Services;
using ThatsMyQuant.Application.Diagnostics;

namespace Listener.Api.Endpoints;

/// <summary>
/// REST endpoint for the Signal Diagnostics page initial load. The page calls this
/// once via <c>useQuery(['signal-diagnostics'])</c> so the table renders immediately
/// (without waiting up to 3s for the first <c>SignalDiagnosticsUpdate</c> SignalR
/// broadcast). Subsequent updates flow through the hub and update the same query
/// cache key via <c>useRealtimeStream</c>.
/// </summary>
public static class SignalDiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapSignalDiagnosticsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/signal-diagnostics")
                             .WithTags("SignalDiagnostics");

        group.MapGet("", GetAll);
        group.MapGet("/by-tier/{tier}", GetByTier);

        return endpoints;
    }

    private static IResult GetAll(ISignalDiagnosticsRegistry registry)
    {
        var snapshots = registry.GetAll();
        var rows = new SignalDiagnosticRowDto[snapshots.Count];
        for (int i = 0; i < snapshots.Count; i++)
            rows[i] = SignalDiagnosticRowDto.From(snapshots[i]);
        return Results.Ok(rows);
    }

    private static IResult GetByTier(ISignalDiagnosticsRegistry registry, string tier)
    {
        if (string.IsNullOrWhiteSpace(tier))
            return Results.BadRequest(new { error = "tier path segment required" });

        var snapshots = registry.GetForTier(tier);
        var rows = new SignalDiagnosticRowDto[snapshots.Count];
        for (int i = 0; i < snapshots.Count; i++)
            rows[i] = SignalDiagnosticRowDto.From(snapshots[i]);
        return Results.Ok(rows);
    }
}
