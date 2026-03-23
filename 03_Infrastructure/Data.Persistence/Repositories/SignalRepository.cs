using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Infrastructure.Persistence;
using ThatsMyQuant.Infrastructure.Services;

namespace ThatsMyQuant.Infrastructure.Repositories;

public class SignalRepository : ISignalRepository
{
    private readonly QuantDbContext _db;
    private readonly SignalWriterChannel _writer;

    public SignalRepository(QuantDbContext db, SignalWriterChannel writer) 
    {
        _db = db;
        _writer = writer;
    }

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
        // 🚀 O(1) LOCK-FREE HOT PATH: Signal physically escapes the executing thread directly into local generic RAM
        _writer.TryWrite(signal);
        await Task.CompletedTask;
    }

    public async Task UpdateAsync(Signal signal)
    {
        // Updates are typically manual and sparse; native EF execution is acceptable here
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
