using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface IExchangeService
{
    Task<Trade> ExecuteOrderAsync(Signal signal);
    Task<Account> GetAccountStatusAsync();
    Task<decimal> GetQueuePositionAsync(string symbol, decimal price, TradeSide side);

    /// <summary>
    /// Returns the user's active/favorited symbols from the exchange as canonical symbols
    /// (uppercase, no hyphens, e.g. <c>"BTCUSDT"</c>).
    /// </summary>
    Task<List<string>> GetFavoritesAsync();

    /// <summary>
    /// Emergency kill switch: cancels ALL open orders on the exchange.
    /// Called by RiskSupervisor and CircuitBreaker during halt events.
    /// Implementations MUST be idempotent and best-effort (log but don't throw on partial failures).
    /// </summary>
    Task<int> CancelAllOrdersAsync(CancellationToken cancellationToken = default);
}
