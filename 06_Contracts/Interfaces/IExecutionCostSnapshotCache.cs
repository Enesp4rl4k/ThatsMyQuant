namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>
/// Cached live execution costs (fees from venue APIs, spread from public market data).
/// Falls back to static MarketExecutionProfile defaults when null.
/// </summary>
public interface IExecutionCostSnapshotCache
{
    /// <summary>Taker fee % per side from venue (e.g. 0.1 for 0.1%).</summary>
    decimal? GetTakerCommissionPercentPerSide(string symbol);

    /// <summary>Full bid–ask spread as % of mid (from book ticker).</summary>
    decimal? GetSpreadPercent(string symbol);

    /// <summary>Execution slip estimate; typically derived from spread (half-spread floor).</summary>
    decimal? GetSlippagePercent(string symbol);

    /// <summary>Refreshes fee + spread for this symbol (call before paper/live decision paths).</summary>
    Task RefreshAsync(string symbol, CancellationToken cancellationToken = default);
}
