using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Application.Observability;
using ThatsMyQuant.Infrastructure.Persistence;

namespace Listener.Api.Endpoints;

/// <summary>
/// Aggregates per-service ServiceHeartbeat rows into a System Health snapshot.
/// A heartbeat is "Healthy" if seen within the last 60s, "Stale" within 5m,
/// "Down" beyond that. Includes exchange health for completeness.
/// </summary>
public static class SystemHealthEndpoints
{
    public static IEndpointRouteBuilder MapSystemHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/system/health", GetHealth).WithTags("System");
        return endpoints;
    }

    private static async Task<IResult> GetHealth(
        QuantDbContext db,
        IExchangeHealthProvider? exchangeHealth,
        CancellationToken ct)
    {
        var heartbeats = await db.ServiceHeartbeats
            .AsNoTracking()
            .OrderBy(s => s.ServiceName)
            .ToArrayAsync(ct);

        var now = DateTime.UtcNow;
        var services = heartbeats.Select(h =>
        {
            var ageSec = (now - h.LastSeenUtc).TotalSeconds;
            string state =
                ageSec <= 60 ? "healthy" :
                ageSec <= 300 ? "stale" :
                "down";

            return new ServiceHealthDto(
                ServiceName: h.ServiceName,
                State: state,
                LastSeenUtc: h.LastSeenUtc,
                StartedUtc: h.StartedUtc,
                UptimeSeconds: (long)Math.Max(0, (now - h.StartedUtc).TotalSeconds),
                AgeSeconds: ageSec,
                Version: h.Version,
                MachineName: h.MachineName,
                ProcessId: h.ProcessId,
                Status: h.Status,
                MemoryMb: Math.Round(h.MemoryBytes / 1_048_576.0, 1));
        }).ToArray();

        ExchangeHealthSummaryDto? exchange = null;
        try
        {
            if (exchangeHealth is not null)
            {
                var s = exchangeHealth.Current;
                exchange = new ExchangeHealthSummaryDto(
                    Venue: s.Venue,
                    IsConnected: s.IsConnected,
                    CredentialsConfigured: s.CredentialsConfigured,
                    IsDemoMode: s.IsDemoMode,
                    LastSuccessUtc: s.LastSuccessUtc,
                    LastError: s.LastError,
                    ConsecutiveFailures: s.ConsecutiveFailures);
            }
        }
        catch
        {
            // Best-effort — don't fail the whole endpoint if exchange health probe is missing.
        }

        var summary = new HealthSummaryDto(
            TotalServices: services.Length,
            HealthyCount: services.Count(s => s.State == "healthy"),
            StaleCount: services.Count(s => s.State == "stale"),
            DownCount: services.Count(s => s.State == "down"),
            Now: now);

        return Results.Ok(new SystemHealthResponse(summary, services, exchange));
    }
}

public sealed record SystemHealthResponse(
    HealthSummaryDto Summary,
    ServiceHealthDto[] Services,
    ExchangeHealthSummaryDto? Exchange);

public sealed record HealthSummaryDto(
    int TotalServices,
    int HealthyCount,
    int StaleCount,
    int DownCount,
    DateTime Now);

public sealed record ServiceHealthDto(
    string ServiceName,
    string State,
    DateTime LastSeenUtc,
    DateTime StartedUtc,
    long UptimeSeconds,
    double AgeSeconds,
    string Version,
    string MachineName,
    int ProcessId,
    string Status,
    double MemoryMb);

public sealed record ExchangeHealthSummaryDto(
    string Venue,
    bool IsConnected,
    bool CredentialsConfigured,
    bool IsDemoMode,
    DateTime? LastSuccessUtc,
    string? LastError,
    int ConsecutiveFailures);
