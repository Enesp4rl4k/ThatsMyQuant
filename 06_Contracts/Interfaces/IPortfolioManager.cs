using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;


/// <summary>
/// Abstraction over the in-memory portfolio manager used by CircuitBreakerService and RiskAgent.
/// </summary>
public interface IPortfolioManager
{
    /// <summary>Returns the current drawdown from peak equity as a percentage.</summary>
    decimal GetDrawdownPercent();

    /// <summary>Returns the available (unlocked) balance for the given asset.</summary>
    decimal GetAvailableBalance(string asset);

    void SetBalance(string asset, decimal amount);
    void AddBalance(string asset, decimal delta);

    void Lock(string asset, decimal amount);
    void Unlock(string asset, decimal amount);

    void OpenPosition(string symbol, decimal entryPrice, decimal quantity, bool isLong);
    decimal ClosePosition(string symbol, decimal exitPrice);

    PositionState? GetPosition(string symbol);
    IReadOnlyDictionary<string, PositionState> GetAllPositions();

    void Reconcile(IReadOnlyDictionary<string, (decimal Total, decimal Locked)> externalBalances);
}
