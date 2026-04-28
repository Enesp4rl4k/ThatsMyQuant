using ThatsMyQuant.Application.Strategy;

namespace Listener.Api.Endpoints;

/// <summary>
/// Strategy diagnostics surface — exposes the in-memory
/// <see cref="StrategyEvaluationLog"/> ring buffer to the web UI's "Why-Not"
/// panel, plus aggregate counts. Read-only; pure projection over the
/// in-memory queue. Returns empty payload until publishers (executors) are
/// wired to push entries — which happens on each strategy tick.
/// </summary>
public static class StrategyEndpoints
{
    public static IEndpointRouteBuilder MapStrategyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/strategy").WithTags("Strategy");
        g.MapGet("/evaluations",          GetEvaluations);
        g.MapGet("/evaluations/{strategy}", GetEvaluationsByStrategy);
        g.MapGet("/evaluations/summary",  GetSummary);
        return endpoints;
    }

    private static IResult GetEvaluations(StrategyEvaluationLog log, int? limit = 50)
    {
        var snapshot = log.Snapshot(limit);
        return Results.Ok(snapshot.Select(MapEntry).ToArray());
    }

    private static IResult GetEvaluationsByStrategy(string strategy, StrategyEvaluationLog log, int? limit = 50)
    {
        var snapshot = log.SnapshotByStrategy(strategy, limit);
        return Results.Ok(snapshot.Select(MapEntry).ToArray());
    }

    /// <summary>Aggregate counts of decisions seen recently — fuel for a stat row.</summary>
    private static IResult GetSummary(StrategyEvaluationLog log)
    {
        var all = log.Snapshot();
        var byDecision = all
            .GroupBy(e => e.Decision)
            .ToDictionary(g => g.Key, g => g.Count());
        var byStrategy = all
            .GroupBy(e => e.Strategy)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Total   = g.Count(),
                    Enter   = g.Count(e => e.Decision == "Enter"),
                    Reject  = g.Count(e => e.Decision == "Reject"),
                    Hold    = g.Count(e => e.Decision == "Hold"),
                    Exit    = g.Count(e => e.Decision == "Exit"),
                });
        return Results.Ok(new { total = all.Count, byDecision, byStrategy });
    }

    private static object MapEntry(StrategyEvaluationEntry e) => new
    {
        utcTime  = e.UtcTime,
        strategy = e.Strategy,
        symbol   = e.Symbol,
        decision = e.Decision,
        reason   = e.Reason,
        metrics  = e.Metrics,
    };
}
