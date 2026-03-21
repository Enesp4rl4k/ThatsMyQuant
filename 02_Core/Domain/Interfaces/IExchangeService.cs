using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Domain.Interfaces;

public interface IExchangeService
{
    Task<Trade> ExecuteOrderAsync(Signal signal);
    Task<Account> GetAccountStatusAsync();
}
