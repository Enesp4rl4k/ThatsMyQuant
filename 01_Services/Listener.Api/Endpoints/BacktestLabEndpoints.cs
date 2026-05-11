using System.Threading;
using ThatsMyQuant.Application.Analytics;
using Listener.Api.Services;

namespace Listener.Api.Endpoints;

/// <summary>
/// Backtest Lab endpoints — pure-math analysis over an equity curve. The page
/// supplies <c>{dates, equityCurve}</c> (typically the output of
/// <c>POST /api/alpha/backtest</c>, but operator can also paste/upload arbitrary
/// curves) and gets back three independent diagnostic views in one shot:
///   - Monte Carlo fan (per-bar percentile envelope)
///   - Linear regression decomposition (trend + residuals + R²)
///   - Shannon entropy trajectory (rolling distribution shape)
///
/// All three services are pure functions — the endpoint just wires them up
/// and returns one combined response so the dashboard makes a single network
/// call. No state, no side effects, no DB writes.
/// </summary>
public static class BacktestLabEndpoints
{
    public static IEndpointRouteBuilder MapBacktestLabEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/backtest-lab").WithTags("BacktestLab");

        group.MapPost("/analyze", Analyze);
        group.MapPost("/interpret", Interpret);

        return endpoints;
    }

    private static async Task<IResult> Interpret(
        BacktestInterpretationRequest req,
        BacktestInterpreterService interpreter,
        ILogger<BacktestInterpreterService> logger,
        CancellationToken ct)
    {
        if (req is null) return Results.BadRequest(new { error = "request body required" });
        try
        {
            var result = await interpreter.InterpretAsync(req, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "BacktestLab interpret failed");
            return Results.Problem(detail: ex.Message, statusCode: 500);
        }
    }

    private static IResult Analyze(
        AnalyzeRequest req,
        MonteCarloFanService mcSvc,
        LinearRegressionDecomposer regSvc,
        ShannonEntropyAnalyzer entSvc,
        ILogger<MonteCarloFanService> logger)
    {
        if (req?.Dates is null || req.EquityCurve is null)
            return Results.BadRequest(new { error = "dates and equityCurve are required" });

        if (req.Dates.Length != req.EquityCurve.Length)
            return Results.BadRequest(new
            {
                error = $"length mismatch: dates ({req.Dates.Length}) vs equityCurve ({req.EquityCurve.Length})",
            });

        if (req.Dates.Length < 2)
            return Results.BadRequest(new { error = "need at least 2 bars to analyze" });

        try
        {
            // MC fan: deterministic when seed provided so the dashboard renders
            // identical fans across reloads. Iterations capped at 5000 to keep
            // worst-case cost bounded (prevents accidental DoS via giant requests).
            var iterations = Math.Clamp(req.MonteCarloIterations ?? 1000, 50, 5000);
            var fan = mcSvc.Compute(
                dates:        req.Dates,
                equityCurve:  req.EquityCurve,
                iterations:   iterations,
                randomSeed:   req.MonteCarloSeed);

            var regression = regSvc.Decompose(req.EquityCurve);

            // Entropy parameters bounded for sanity. Window must be < N (otherwise
            // returns all-NaN, technically valid but useless to render).
            var window = Math.Clamp(req.EntropyWindow ?? 30, 5, Math.Max(5, req.EquityCurve.Length / 2));
            var bins = Math.Clamp(req.EntropyBins ?? 10, 2, 50);
            var entropy = entSvc.Analyse(req.Dates, req.EquityCurve, window, bins);

            return Results.Ok(new AnalyzeResponse(
                MonteCarloFan: fan,
                Regression:    regression,
                Entropy:       entropy));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "BacktestLab analyze failed");
            return Results.Problem(detail: ex.Message, statusCode: 500);
        }
    }
}

/// <summary>
/// Request DTO. Defaults are applied server-side if a knob is omitted, so the
/// minimum viable payload is just <c>{dates, equityCurve}</c>.
/// </summary>
public sealed record AnalyzeRequest(
    DateTime[] Dates,
    decimal[]  EquityCurve,
    int?       MonteCarloIterations = null,
    int?       MonteCarloSeed       = null,
    int?       EntropyWindow        = null,
    int?       EntropyBins          = null);

/// <summary>
/// Combined response — one round trip, three independent views.
/// </summary>
public sealed record AnalyzeResponse(
    MonteCarloFanResult     MonteCarloFan,
    LinearRegressionResult  Regression,
    ShannonEntropyResult    Entropy);
