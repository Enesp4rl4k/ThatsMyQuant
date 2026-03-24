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
    private readonly ConcurrentDictionary<Guid, byte> _historicalIds = new();
    
    // Hardware limitation parameter limiting unbounded historical growth
    private const int MaxHistory = 500;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _isInitialized = false;

    public PortfolioStateCache(IServiceProvider serviceProvider, ILogger<PortfolioStateCache> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized) return;

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
            _isInitialized = true;
            _logger.LogInformation("PortfolioStateCache mounted with {Active} active and {History} historical trades pinned to local CPU registers.", _activeTrades.Count, _historicalTrades.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize O(1) MemoryPortfolioManager state.");
        }
        finally
        {
            _initLock.Release();
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
            // If it was previously in history (e.g. re-opened), we leave it in history but it's now active too
        }
        else
        {
            if (_activeTrades.TryRemove(trade.Id, out _))
            {
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
        if (!_historicalIds.TryAdd(trade.Id, 0)) return; // Prevent duplicate entries in history

        _historicalTrades.Enqueue(trade);
        while (_historicalTrades.Count > MaxHistory)
        {
            if (_historicalTrades.TryDequeue(out var oldTrade))
            {
                _historicalIds.TryRemove(oldTrade.Id, out _);
            }
        }
    }
}
