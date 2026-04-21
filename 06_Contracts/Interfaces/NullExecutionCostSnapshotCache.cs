namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>Default when no live venue cache is wired; static profiles apply.
/// </summary>
public sealed class NullExecutionCostSnapshotCache : IExecutionCostSnapshotCache
{
    public decimal? GetTakerCommissionPercentPerSide(string symbol) => null;

    public decimal? GetSpreadPercent(string symbol) => null;

    public decimal? GetSlippagePercent(string symbol) => null;

    public Task RefreshAsync(string symbol, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
