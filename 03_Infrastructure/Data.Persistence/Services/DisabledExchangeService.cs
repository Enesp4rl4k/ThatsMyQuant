using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

public class DisabledExchangeService : IExchangeService
{
    public Task<Trade> ExecuteOrderAsync(Signal signal)
    {
        throw new InvalidOperationException(
            "Live execution is disabled. Configure a real exchange service or enable explicit simulation mode.");
    }

    public Task<Account> GetAccountStatusAsync()
    {
        return Task.FromResult(new Account
        {
            Id = "DISABLED",
            Provider = "Disabled",
            Balance = 0m,
            Equity = 0m
        });
    }
}
