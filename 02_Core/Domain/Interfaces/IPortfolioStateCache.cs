using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Domain.Interfaces;

public interface IPortfolioStateCache
{
    IReadOnlyList<Trade> GetRecentTrades();
    void AddOrUpdateTrade(Trade trade);
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
