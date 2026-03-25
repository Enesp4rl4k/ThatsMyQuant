using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Infrastructure.Repositories;

public class SignalRepository : ISignalRepository
{
    private readonly QuantDbContext _db;

    public SignalRepository(QuantDbContext db) => _db = db;

    public async Task<Signal?> GetByIdAsync(Guid id)
        => await _db.Signals.FindAsync(id);

    public async Task<IReadOnlyList<Signal>> GetRecentAsync(int count = 50)
        => await _db.Signals
            .OrderByDescending(s => s.Timestamp)
            .Take(count)
            .ToListAsync();

    public async Task<IReadOnlyList<Signal>> GetByStatusAsync(TradeStatus status, int count = 50)
        => await _db.Signals
            .Where(s => s.Status == status)
            .OrderBy(s => s.Timestamp)
            .Take(count)
            .ToListAsync();

    public async Task AddAsync(Signal signal)
    {
        await _db.Signals.AddAsync(signal);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(Signal signal)
    {
        _db.Signals.Update(signal);
        await _db.SaveChangesAsync();
    }

    public async Task<Dictionary<TradeStatus, int>> GetStatusCountsAsync()
    {
        return await _db.Signals
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count);
    }
}
