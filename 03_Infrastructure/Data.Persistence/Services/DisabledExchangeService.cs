using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

public class DisabledExchangeService : IExchangeService
{
    public Task<Trade> ExecuteOrderAsync(Signal signal, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(
            "Live execution is disabled. Configure a real exchange service or enable explicit simulation mode.");
    }

    public Task<Account> GetAccountStatusAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Account
        {
            Id = "DISABLED",
            Provider = "Disabled",
            Balance = 0m,
            Equity = 0m
        });
    }

    public Task<decimal> GetQueuePositionAsync(string symbol, decimal price, ThatsMyQuant.Contracts.Enums.TradeSide side, CancellationToken cancellationToken = default) => Task.FromResult(0m);
}
