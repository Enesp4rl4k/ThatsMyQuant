namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Exposes a pair engine's live statistical state. Consumed by the portfolio
/// coordinator for capital allocation decisions and by the dashboard for
/// real-time visualisation of pair health.
/// </summary>
public interface IPairDiagnosticsProvider
{
    PairDiagnosticSnapshot GetDiagnostics();
}

/// <summary>
/// Portfolio-level view aggregating all pair engines under a coordinator.
/// Surface for UI dashboards.
/// </summary>
public interface IPairPortfolioDiagnostics
{
    IReadOnlyList<PairDiagnosticSnapshot> GetAllPairDiagnostics();
    PairPortfolioSummary GetPortfolioSummary();
}

/// <summary>Aggregate stats across all pairs managed by the coordinator.</summary>
/// <param name="TotalPairs">Count of registered pair engines.</param>
/// <param name="ActivePairs">Pairs currently admitted (cointegrated).</param>
/// <param name="OpenPositions">Pairs in ShortSpread/LongSpread state.</param>
/// <param name="AverageHurst">Mean Hurst across active pairs (lower=better).</param>
/// <param name="AverageHalfLife">Mean half-life across active pairs.</param>
/// <param name="TotalAllocationScale">Sum of active CoordinatorAllocationScale values — sanity check for budget.</param>
public sealed record PairPortfolioSummary(
    int TotalPairs,
    int ActivePairs,
    int OpenPositions,
    double AverageHurst,
    double AverageHalfLife,
    decimal TotalAllocationScale);
