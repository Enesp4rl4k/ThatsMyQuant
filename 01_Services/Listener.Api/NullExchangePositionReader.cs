using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace Listener.Api;

internal sealed class NullExchangePositionReader : IExchangePositionReader
{
    public Task<IReadOnlyList<ExchangePositionSnapshot>> GetOpenPositionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ExchangePositionSnapshot>>(Array.Empty<ExchangePositionSnapshot>());
}
