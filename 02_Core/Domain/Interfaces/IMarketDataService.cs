using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Domain.Interfaces;

public interface IMarketDataService
{
    Task<decimal> GetLivePriceAsync(string symbol, string market = "BIST");
    Task<Dictionary<string, string>> GetTechnicalAnalysisAsync(string symbol, string market = "BIST");
    Task<List<string>> GetLatestNewsAsync(string symbol);

    /// <summary>
    /// Fetch historical OHLCV candle data for indicator computation.
    /// Supports dynamic timeframes (e.g., "1d", "1h", "5m").
    /// </summary>
    Task<List<Candle>> GetCandlesAsync(string symbol, int count = 100, string market = "BIST", string timeframe = "1d");
}
