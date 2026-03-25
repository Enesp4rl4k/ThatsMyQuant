using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface IExchangeService
{
    Task<Trade> ExecuteOrderAsync(Signal signal);
    Task<Account> GetAccountStatusAsync();
}
