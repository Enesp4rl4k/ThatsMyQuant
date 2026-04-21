namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>
/// Supplies perpetual-swap funding rate observations. Funding is paid every 8h on OKX
/// and expresses the premium longs pay shorts (positive) or shorts pay longs (negative).
/// Extreme positive funding = crowded longs = mean-reversion setup for shorts.
///
/// Implementations:
///  - NullFundingRateProvider — always returns 0 (no data, no gate).
///  - FileFundingRateProvider — reads a time-series CSV keyed by (symbol, ts).
///  - OkxFundingRateProvider  — live API fetch (future wiring).
/// </summary>
public interface IFundingRateProvider
{
    /// <summary>
    /// Returns the funding rate effective at <paramref name="asOfUtc"/> for <paramref name="symbol"/>,
    /// expressed as a fraction per 8h (e.g. 0.0001 = 1 bp per 8h). Returns 0 if unknown.
    /// </summary>
    decimal GetRate(string symbol, DateTime asOfUtc);

    /// <summary>
    /// Optional warm-up hook. Implementations with remote data (OKX) should paginate history
    /// for <paramref name="symbols"/> down to <paramref name="fromUtc"/> and cache results so
    /// subsequent <see cref="GetRate"/> calls on the backtest hot loop are pure in-memory.
    /// Default no-op for null/file providers.
    /// </summary>
    Task PrefetchAsync(IEnumerable<string> symbols, DateTime fromUtc, CancellationToken ct = default)
        => Task.CompletedTask;
}

public sealed class NullFundingRateProvider : IFundingRateProvider
{
    public decimal GetRate(string symbol, DateTime asOfUtc) => 0m;
}
