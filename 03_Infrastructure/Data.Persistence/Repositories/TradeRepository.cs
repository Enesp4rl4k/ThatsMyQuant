using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Infrastructure.Repositories;

public class TradeRepository : ITradeRepository
{
    private readonly QuantDbContext _db;

    public TradeRepository(QuantDbContext db) => _db = db;

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
    }

    public async Task UpdateAsync(Trade trade)
    {
        _db.Trades.Update(trade);
        await _db.SaveChangesAsync();
    }
}
