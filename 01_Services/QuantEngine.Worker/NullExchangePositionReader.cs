using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>Fallback when the active IExchangeService doesn't implement IExchangePositionReader.
/// Returns an empty list so reconciliation logs a warning but doesn't crash.</summary>
internal sealed class NullExchangePositionReader : IExchangePositionReader
{
    public Task<IReadOnlyList<ExchangePositionSnapshot>> GetOpenPositionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ExchangePositionSnapshot>>(Array.Empty<ExchangePositionSnapshot>());
}
