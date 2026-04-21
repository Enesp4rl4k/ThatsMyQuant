using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// A high-speed execution engine completely disconnected from internet exchanges natively mapping Trade Fills instantly.
/// This fulfills the Market Replay boundary so Backtests assume zero slippage execution directly matching the CSV cursor.
/// </summary>
public class ReplayBrokerService : IExchangeService
{
    private readonly ILogger<ReplayBrokerService> _logger;
    private readonly ISystemClock _clock;

    public ReplayBrokerService(ILogger<ReplayBrokerService> logger, ISystemClock clock)
    {
        _logger = logger;
        _clock = clock;
    }

    public Task<Trade> ExecuteOrderAsync(Signal signal)
    {
        _logger.LogInformation("ReplayBrokerService executing {Action} natively offline for {Symbol} at {Price}", signal.ExecutionAction, signal.Symbol, signal.Price);
        
        var trade = new Trade
        {
            Id = Guid.NewGuid(),
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            Side = signal.Side,
            Quantity = signal.Quantity > 0m ? signal.Quantity : 1.0m,
            FilledQuantity = signal.Quantity > 0m ? signal.Quantity : 1.0m,
            RemainingQuantity = signal.Quantity > 0m ? signal.Quantity : 1.0m,
            RequestedQuantity = signal.Quantity > 0m ? signal.Quantity : 1.0m,
            EntryPrice = signal.Price,
            EntryTime = _clock.UtcNow, 
            Status = TradeStatus.Executed,
            LifecycleStatus = TradeLifecycleStatus.Active,
            StrategyTag = signal.StrategyTag,
            RegimeTag = signal.RegimeTag,
            ExecutionMode = "REPLAY",
            Leverage = ResolveLeverage(signal),
            MarginMode = ResolveMarginMode(signal)
        };

        return Task.FromResult(trade);
    }

    public Task<Account> GetAccountStatusAsync()
    {
        return Task.FromResult(new Account
        {
            Balance = 100_000m,
            Equity = 100_000m
        });
    }

    public bool TryGetLatestOrderBook(string symbol, out OrderBookSnapshot book)
    {
        book = default;
        return false;
    }

    public Task<decimal> GetQueuePositionAsync(string symbol, decimal price, TradeSide side) => Task.FromResult(0m);

    public Task<List<string>> GetFavoritesAsync() => Task.FromResult(new List<string>());

    public Task<int> CancelAllOrdersAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

    private static decimal ResolveLeverage(Signal signal)
    {
        if (signal.Metadata.TryGetValue("Leverage", out var leverageRaw) &&
            decimal.TryParse(leverageRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var leverage) &&
            leverage > 0m)
        {
            return leverage;
        }

        return 1m;
    }

    private static MarginMode ResolveMarginMode(Signal signal)
    {
        if (signal.Metadata.TryGetValue("MarginMode", out var marginRaw) &&
            Enum.TryParse<MarginMode>(marginRaw, true, out var marginMode))
        {
            return marginMode;
        }

        return MarginMode.Isolated;
    }
}
