using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Infrastructure.Repositories;

public class TradeRepository : ITradeRepository
{
    private readonly QuantDbContext _db;
    private readonly IPortfolioStateCache _cache;
    private readonly RiskAgent? _riskAgent;

    public TradeRepository(QuantDbContext db, IPortfolioStateCache cache, RiskAgent? riskAgent = null)
    {
        _db = db;
        _cache = cache;
        _riskAgent = riskAgent;
    }

    public async Task<Trade?> GetByIdAsync(Guid id)
        => await _db.Trades.FindAsync(id);

    public async Task<Trade?> GetBySignalIdAsync(Guid signalId)
        => await _db.Trades.FirstOrDefaultAsync(t => t.SignalId == signalId);

    public async Task<IReadOnlyList<Trade>> GetAllAsync(int count = 100)
        => await _db.Trades
            .OrderByDescending(t => t.EntryTime)
            .Take(count)
            .ToListAsync();

    public async Task AddAsync(Trade trade)
    {
        await _db.Trades.AddAsync(trade);
        await _db.SaveChangesAsync();
        _cache.AddOrUpdateTrade(trade); // O(1) Ram pointer tracking
        _riskAgent?.NotifyTradeUpdate(trade);
    }

    public async Task UpdateAsync(Trade trade)
    {
        _db.Trades.Update(trade);
        await _db.SaveChangesAsync();
        _cache.AddOrUpdateTrade(trade); // O(1) Ram pointer tracking
        _riskAgent?.NotifyTradeUpdate(trade);
    }
}
