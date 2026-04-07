using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Enums;

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
            Side = signal.Side,
            EntryPrice = signal.Price,
            Quantity = signal.Quantity > 0m ? signal.Quantity : 1.0m,
            FilledQuantity = signal.Quantity > 0m ? signal.Quantity : 1.0m,
            RemainingQuantity = signal.Quantity > 0m ? signal.Quantity : 1.0m,
            RequestedQuantity = signal.Quantity > 0m ? signal.Quantity : 1.0m,
            EntryTime = DateTime.UtcNow,
            Status = TradeStatus.Executed,
            LifecycleStatus = TradeLifecycleStatus.Active,
            ExecutionMode = "SIM"
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

    public Task<List<string>> GetFavoritesAsync()
    {
        return Task.FromResult(new List<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT" });
    }

    public Task<int> CancelAllOrdersAsync(CancellationToken cancellationToken = default)
    {
        var count = _trades.Count;
        _trades.Clear();
        _logger.LogWarning("🚨 Mock emergency halt: {Count} simulated orders cancelled.", count);
        return Task.FromResult(count);
    }
}
