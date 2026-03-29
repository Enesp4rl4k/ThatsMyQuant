using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface IExchangeService
{
    Task<Trade> ExecuteOrderAsync(Signal signal);
    Task<Account> GetAccountStatusAsync();
    Task<decimal> GetQueuePositionAsync(string symbol, decimal price, TradeSide side);
}
