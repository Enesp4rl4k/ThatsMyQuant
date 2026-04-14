namespace ThatsMyQuant.Contracts.Interfaces;

using ThatsMyQuant.Contracts.Models;

/// <summary>
/// Service for fetching large historical datasets from exchanges.
/// Handles pagination and rate limiting for backtest data preparation.
/// </summary>
public interface IHistoricalMarketDataService
{
    /// <summary>
    /// Fetches historical candles (OHLCV) for a specific time range.
    /// Handles borsa-specific limits (e.g., 100 per request) automatically.
    /// </summary>
    Task<List<Candle>> GetHistoricalCandlesAsync(
        string symbol,
        string timeframe,
        DateTime start,
        DateTime end,
        CancellationToken ct = default);
}
