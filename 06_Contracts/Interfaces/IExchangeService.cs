using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface IExchangeService
{
    Task<Trade> ExecuteOrderAsync(Signal signal, CancellationToken cancellationToken = default);
    Task<Account> GetAccountStatusAsync(CancellationToken cancellationToken = default);
    Task<decimal> GetQueuePositionAsync(string symbol, decimal price, TradeSide side, CancellationToken cancellationToken = default);
}
