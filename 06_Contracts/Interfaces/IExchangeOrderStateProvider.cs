using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface IExchangeOrderStateProvider
{
    Task<IReadOnlyList<OpenOrderSnapshot>> GetOpenOrdersAsync(CancellationToken cancellationToken = default);
}
