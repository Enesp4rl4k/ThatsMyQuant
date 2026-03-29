namespace ThatsMyQuant.Contracts.Interfaces;

public interface IPortfolioManager
{
    void SetBalance(string asset, decimal amount);
    void AddBalance(string asset, decimal delta);
    decimal GetTotalBalance(string asset);
    void Lock(string asset, decimal amount);
    void Unlock(string asset, decimal amount);
    decimal GetAvailableBalance(string asset);
    void Clear();
}
