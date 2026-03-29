using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

public class MockBrokerService : IExchangeService
{
    private readonly ILogger<MockBrokerService> _logger;
    private readonly Dictionary<Guid, Trade> _trades = new();

    public MockBrokerService(ILogger<MockBrokerService> logger)
    {
        _logger = logger;
    }

    public Task<Trade> ExecuteOrderAsync(Signal signal)
    {
        var trade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            EntryPrice = signal.Price,
            Quantity = 1.0m,
            EntryTime = DateTime.UtcNow
        };

        _trades[trade.Id] = trade;
        _logger.LogInformation("Mock Execution: {TradeId} for {Symbol} @ {Price}", trade.Id, trade.Symbol, trade.EntryPrice);
        return Task.FromResult(trade);
    }

    public Task<Account> GetAccountStatusAsync()
    {
        return Task.FromResult(new Account
        {
            Id = "MOCK-001",
            Provider = "Mock",
            Balance = 10000m,
            Equity = 10000m
        });
    }

    public Task<decimal> GetQueuePositionAsync(string symbol, decimal price, ThatsMyQuant.Contracts.Enums.TradeSide side) => Task.FromResult(0m);
}
