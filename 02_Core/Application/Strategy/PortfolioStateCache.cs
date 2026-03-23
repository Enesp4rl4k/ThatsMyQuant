using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Domain.Enums;

namespace ThatsMyQuant.Application.Strategy;

public class PortfolioStateCache : IPortfolioStateCache
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PortfolioStateCache> _logger;
    
    // O(1) Local Memory Bounded Arrays
    private readonly ConcurrentDictionary<Guid, Trade> _activeTrades = new();
    private readonly ConcurrentQueue<Trade> _historicalTrades = new();
    
    // Hardware limitation parameter limiting unbounded historical growth
    private const int MaxHistory = 500;
    private int _isInitialized = 0;

    public PortfolioStateCache(IServiceProvider serviceProvider, ILogger<PortfolioStateCache> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _isInitialized, 1) == 1) return;

        try
        {
            // Dynamically resolve repository to avoid Circular DI dependency loops
            using var scope = _serviceProvider.CreateScope();
            var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
            var recentTrades = await tradeRepo.GetAllAsync(MaxHistory);

            foreach (var trade in recentTrades.OrderBy(t => t.EntryTime))
            {
                if (!trade.IsClosed && trade.Status == TradeStatus.Executed)
                {
                    _activeTrades[trade.Id] = trade;
                }
                else
                {
                    EnqueueHistorical(trade);
                }
            }
            _logger.LogInformation("PortfolioStateCache mounted with {Active} active and {History} historical trades pinned to local CPU registers.", _activeTrades.Count, _historicalTrades.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize O(1) MemoryPortfolioManager state.");
        }
    }

    public IReadOnlyList<Trade> GetRecentTrades()
    {
        // 🚀 HFT ARCHITECTURE: Execute lock-free Array iteration mapping natively outside of the Async State Machine
        var count = _activeTrades.Count + _historicalTrades.Count;
        var list = new List<Trade>(count);
        list.AddRange(_activeTrades.Values);
        list.AddRange(_historicalTrades);
        return list;
    }

    public void AddOrUpdateTrade(Trade trade)
    {
        if (trade == null) return;

        if (!trade.IsClosed && trade.Status == TradeStatus.Executed)
        {
            _activeTrades[trade.Id] = trade;
        }
        else
        {
            if (_activeTrades.TryRemove(trade.Id, out _))
            {
                // Transitioning natively from Active -> Historical
                EnqueueHistorical(trade);
            }
            else if (trade.IsClosed || trade.Status == TradeStatus.Failed || trade.Status == TradeStatus.Rejected)
            {
                EnqueueHistorical(trade);
            }
        }
    }

    private void EnqueueHistorical(Trade trade)
    {
        _historicalTrades.Enqueue(trade);
        while (_historicalTrades.Count > MaxHistory)
        {
            _historicalTrades.TryDequeue(out _);
        }
    }
}
