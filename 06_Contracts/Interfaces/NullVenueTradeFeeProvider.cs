namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>No live fee API (mock / offline); commission falls back to static profiles.
/// </summary>
public sealed class NullVenueTradeFeeProvider : IVenueTradeFeeProvider
{
    public Task<decimal?> GetTakerCommissionPercentPerSideAsync(string symbol, CancellationToken cancellationToken = default)
        => Task.FromResult<decimal?>(null);
}
