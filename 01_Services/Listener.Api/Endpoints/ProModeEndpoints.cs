using ThatsMyQuant.Application.Strategy.ProMode;
using ThatsMyQuant.Contracts.Enums;

namespace Listener.Api.Endpoints;

public static class ProModeEndpoints
{
    public static IEndpointRouteBuilder MapProModeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/pro-mode").WithTags("ProMode");

        group.MapGet("/snapshot", GetSnapshot);
        group.MapPost("/arm", Arm);
        group.MapPost("/disarm", Disarm);
        group.MapPost("/kill", Kill);
        group.MapPost("/scan", ForceScan);
        group.MapPost("/regime", UpdateRegime);

        return endpoints;
    }

    private static IResult GetSnapshot(ProModeOrchestrator orchestrator)
    {
        var snap = orchestrator.Snapshot();
        return Results.Ok(MapSnapshot(snap));
    }

    private static IResult Arm(ArmRequest req, ProModeOrchestrator orchestrator)
    {
        if (req.BaseLeverage <= 0m)
        {
            return Results.BadRequest(new { error = "baseLeverage must be > 0" });
        }
        orchestrator.Arm(req.BaseLeverage);
        return Results.Ok(MapSnapshot(orchestrator.Snapshot()));
    }

    private static IResult Disarm(OperatorNoteRequest req, ProModeOrchestrator orchestrator)
    {
        orchestrator.Disarm(req.OperatorNote ?? "web");
        return Results.Ok(MapSnapshot(orchestrator.Snapshot()));
    }

    private static IResult Kill(OperatorNoteRequest req, ProModeOrchestrator orchestrator)
    {
        orchestrator.EmergencyKill(req.OperatorNote ?? "web-kill");
        return Results.Ok(MapSnapshot(orchestrator.Snapshot()));
    }

    private static IResult ForceScan(ProModeOrchestrator orchestrator)
    {
        var ops = orchestrator.ScanCycle(DateTime.UtcNow);
        return Results.Ok(new
        {
            opportunityCount = ops.Count,
            snapshot = MapSnapshot(orchestrator.Snapshot()),
        });
    }

    private static IResult UpdateRegime(RegimeRequest req, ProModeOrchestrator orchestrator)
    {
        if (!Enum.TryParse<MarketRegime>(req.Regime, ignoreCase: true, out var regime))
        {
            return Results.BadRequest(new { error = $"unknown regime '{req.Regime}'" });
        }
        orchestrator.UpdateRegime(regime);
        return Results.Ok(MapSnapshot(orchestrator.Snapshot()));
    }

    private static ProModeSnapshotDto MapSnapshot(ProModeSnapshot s) => new(
        IsArmed: s.IsArmed,
        CircuitBreaker: new CircuitBreakerSnapshotDto(
            State: s.CircuitBreaker.State.ToString(),
            RealizedDrawdown: s.CircuitBreaker.RealizedDrawdown,
            PeakEquity: s.CircuitBreaker.PeakEquity,
            CurrentEquity: s.CircuitBreaker.CurrentEquity,
            LastTripReason: s.CircuitBreaker.LastTripReason,
            LastTripUtc: s.CircuitBreaker.LastTripUtc),
        CurrentRegime: s.CurrentRegime.ToString(),
        BaseLeverage: s.BaseLeverage,
        EffectiveLeverage: s.EffectiveLeverage,
        LeverageExplanation: s.LeverageExplanation,
        RecentOpportunities: s.RecentOpportunities.Select(o => new FundingOpportunityDto(
            Symbol: o.Symbol,
            FundingRate: o.FundingRate,
            FundingBps: o.FundingBps,
            Conviction: o.Conviction.ToString(),
            Direction: o.Direction.ToString(),
            ObservedUtc: o.ObservedUtc)).ToList(),
        CandidatePool: s.CandidatePool.Select(c => new PromotionCandidateDto(
            Symbol: c.Symbol,
            StrategyTag: c.StrategyTag,
            FirstSignalUtc: c.FirstSignalUtc,
            State: c.State.ToString(),
            OosSharpe: c.OosSharpe,
            OosProfitFactor: c.OosProfitFactor,
            OosTrades: c.OosTrades,
            RejectionReason: c.RejectionReason)).ToList());
}

public sealed record ArmRequest(decimal BaseLeverage);
public sealed record OperatorNoteRequest(string? OperatorNote);
public sealed record RegimeRequest(string Regime);

public sealed record ProModeSnapshotDto(
    bool IsArmed,
    CircuitBreakerSnapshotDto CircuitBreaker,
    string CurrentRegime,
    decimal BaseLeverage,
    decimal EffectiveLeverage,
    string LeverageExplanation,
    IReadOnlyList<FundingOpportunityDto> RecentOpportunities,
    IReadOnlyList<PromotionCandidateDto> CandidatePool);

public sealed record CircuitBreakerSnapshotDto(
    string State,
    decimal RealizedDrawdown,
    decimal PeakEquity,
    decimal CurrentEquity,
    string? LastTripReason,
    DateTime? LastTripUtc);

public sealed record FundingOpportunityDto(
    string Symbol,
    decimal FundingRate,
    double FundingBps,
    string Conviction,
    string Direction,
    DateTime ObservedUtc);

public sealed record PromotionCandidateDto(
    string Symbol,
    string StrategyTag,
    DateTime FirstSignalUtc,
    string State,
    double OosSharpe,
    double OosProfitFactor,
    int OosTrades,
    string? RejectionReason);
