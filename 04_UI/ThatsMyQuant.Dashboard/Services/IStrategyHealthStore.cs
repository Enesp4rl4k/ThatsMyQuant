namespace ThatsMyQuant.Dashboard.Services;

public interface IStrategyHealthStore
{
    IReadOnlyList<StrategyHealthSnapshot> Load();
    void Save(IEnumerable<StrategyHealthSnapshot> snapshots);
}
