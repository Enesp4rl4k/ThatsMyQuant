using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Persistence;
using ThatsMyQuant.Infrastructure.Services;

namespace ThatsMyQuant.Infrastructure.Repositories;

public class SignalRepository : ISignalRepository
{
    private static readonly Func<QuantDbContext, int, IAsyncEnumerable<Signal>> s_getRecentQuery =
        EF.CompileAsyncQuery((QuantDbContext db, int count) =>
            db.Signals
                .AsNoTracking()
                .OrderByDescending(s => s.Timestamp)
                .Take(count));

    private static readonly Func<QuantDbContext, TradeStatus, int, IAsyncEnumerable<Signal>> s_getByStatusQuery =
        EF.CompileAsyncQuery((QuantDbContext db, TradeStatus status, int count) =>
            db.Signals
                .AsNoTracking()
                .Where(s => s.Status == status)
                .OrderBy(s => s.Timestamp)
                .Take(count));

    private static readonly Func<QuantDbContext, IAsyncEnumerable<SignalStatusCount>> s_getStatusCountsQuery =
        EF.CompileAsyncQuery((QuantDbContext db) =>
            db.Signals
                .AsNoTracking()
                .GroupBy(s => s.Status)
                .Select(g => new SignalStatusCount(g.Key, g.Count())));

    private readonly QuantDbContext _db;
    private readonly SignalWriterChannel? _signalWriterChannel;

    public SignalRepository(QuantDbContext db, SignalWriterChannel? signalWriterChannel = null)
    {
        _db = db;
        _signalWriterChannel = signalWriterChannel;
    }

    public async Task<Signal?> GetByIdAsync(Guid id)
        => await _db.Signals.FindAsync(id);

    public async Task<IReadOnlyList<Signal>> GetRecentAsync(int count = 50)
        => await MaterializeAsync(s_getRecentQuery(_db, count), count);

    public async Task<IReadOnlyList<Signal>> GetByStatusAsync(TradeStatus status, int count = 50)
        => await MaterializeAsync(s_getByStatusQuery(_db, status, count), count);

    public async Task AddAsync(Signal signal)
    {
        if (_signalWriterChannel?.TryWrite(SignalPersistenceBatchWriter.Clone(signal)) == true)
        {
            return;
        }

        await SignalPersistenceBatchWriter.UpsertAsync(_db, signal);
    }

    public async Task UpdateAsync(Signal signal)
    {
        if (_signalWriterChannel?.TryWrite(SignalPersistenceBatchWriter.Clone(signal)) == true)
        {
            return;
        }

        await SignalPersistenceBatchWriter.UpsertAsync(_db, signal);
    }

    public async Task<Dictionary<TradeStatus, int>> GetStatusCountsAsync()
    {
        var counts = new Dictionary<TradeStatus, int>();
        await foreach (var row in s_getStatusCountsQuery(_db))
        {
            counts[row.Status] = row.Count;
        }

        return counts;
    }

    private static async Task<IReadOnlyList<Signal>> MaterializeAsync(IAsyncEnumerable<Signal> query, int capacity)
    {
        var signals = new List<Signal>(Math.Max(capacity, 0));
        await foreach (var signal in query)
        {
            signals.Add(signal);
        }

        return signals;
    }

    private readonly record struct SignalStatusCount(TradeStatus Status, int Count);
}
