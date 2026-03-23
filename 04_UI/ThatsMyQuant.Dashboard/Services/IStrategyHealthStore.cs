namespace ThatsMyQuant.Dashboard.Services;

public interface IStrategyHealthStore
{
    Task<IReadOnlyList<StrategyHealthSnapshot>> LoadAsync();
    Task SaveAsync(IEnumerable<StrategyHealthSnapshot> snapshots);
}

