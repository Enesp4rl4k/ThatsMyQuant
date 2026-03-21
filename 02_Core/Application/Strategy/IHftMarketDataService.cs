using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Application.Strategy;

public interface IHftMarketDataService
{
    Task<List<Candle>> GetCandlesAsync(Signal signal, string market, CancellationToken cancellationToken = default);
}
