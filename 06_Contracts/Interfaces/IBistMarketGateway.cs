using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface IBistMarketGateway
{
    Task<decimal> GetLivePriceAsync(string symbol, CancellationToken cancellationToken = default);
    Task<Dictionary<string, string>> GetTechnicalAnalysisAsync(string symbol, CancellationToken cancellationToken = default);
    Task<List<string>> GetLatestNewsAsync(string symbol, CancellationToken cancellationToken = default);
    Task<List<Candle>> GetCandlesAsync(string symbol, int count = 100, string timeframe = "1d", CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetUniverseAsync(CancellationToken cancellationToken = default);
}
