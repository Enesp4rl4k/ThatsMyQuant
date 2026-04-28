using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Infrastructure.Persistence;

namespace Listener.Api.Endpoints;

/// <summary>
/// Read-only access to the cross-process operator alert event log.
/// Powers the Audit Log dashboard page (/audit).
/// </summary>
public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/audit").WithTags("Audit");
        group.MapGet("/events", GetEvents);
        group.MapGet("/sources", GetSources);
        return endpoints;
    }

    private static async Task<IResult> GetEvents(
        QuantDbContext db,
        [FromQuery] string? severity,
        [FromQuery] string? source,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 1000);

        var q = db.OperatorAlertEvents.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(severity)) q = q.Where(e => e.Severity == severity);
        if (!string.IsNullOrWhiteSpace(source)) q = q.Where(e => e.Source == source);
        if (from.HasValue) q = q.Where(e => e.UtcTime >= from.Value);
        if (to.HasValue) q = q.Where(e => e.UtcTime <= to.Value);

        var rows = await q
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .Select(e => new AuditEventDto(
                e.Id,
                e.UtcTime,
                e.Severity,
                e.Source,
                e.Title,
                e.Body,
                e.ProcessName,
                e.CorrelationId))
            .ToArrayAsync(ct);

        return Results.Ok(rows);
    }

    private static async Task<IResult> GetSources(QuantDbContext db, CancellationToken ct)
    {
        var sources = await db.OperatorAlertEvents
            .AsNoTracking()
            .Select(e => e.Source)
            .Distinct()
            .OrderBy(s => s)
            .ToArrayAsync(ct);
        return Results.Ok(sources);
    }
}

public sealed record AuditEventDto(
    long Id,
    DateTime UtcTime,
    string Severity,
    string Source,
    string Title,
    string Body,
    string ProcessName,
    string? CorrelationId);
