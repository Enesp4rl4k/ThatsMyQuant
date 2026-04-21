using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Safe fallback exchange service used when no exchange credentials are configured.
/// Records orders as locally-failed (SAFEMODE_REJECT) instead of throwing, so paper/backtest
/// paths and supervisory loops can still run without crashing on misconfiguration.
/// </summary>
public class DisabledExchangeService : IExchangeService
{
    private readonly ILogger<DisabledExchangeService> _logger;

    public DisabledExchangeService(ILogger<DisabledExchangeService>? logger = null)
    {
        _logger = logger ?? NullLogger<DisabledExchangeService>.Instance;
    }

    public Task<Trade> ExecuteOrderAsync(Signal signal)
    {
        _logger.LogWarning(
            "DisabledExchangeService: rejecting order for {Symbol} ({Side} @ {Price}) — no exchange credentials configured.",
            signal.Symbol, signal.Side, signal.Price);

        var trade = new Trade
        {
            SignalId = signal.Id,
            Symbol = signal.Symbol,
            Side = signal.Side,
            Status = TradeStatus.Failed,
            LifecycleStatus = TradeLifecycleStatus.Rejected,
            EntryPrice = 0m,
            Quantity = 0m,
            ExecutionMode = "SAFEMODE",
            PromotionStage = "Backtest",
            VenueExecutionScope = "SAFEMODE_REJECT: no exchange credentials configured.",
            Leverage = ResolveLeverage(signal),
            MarginMode = ResolveMarginMode(signal)
        };
        return Task.FromResult(trade);
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
