using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Contracts.Exceptions;
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

    public async Task<IReadOnlyList<Trade>> GetOpenAsync(int count = 100)
        => await _db.Trades
            .AsNoTracking()
            .Where(t =>
                t.LifecycleStatus == Contracts.Enums.TradeLifecycleStatus.Submitted ||
                t.LifecycleStatus == Contracts.Enums.TradeLifecycleStatus.Accepted ||
                t.LifecycleStatus == Contracts.Enums.TradeLifecycleStatus.PartiallyFilled ||
                t.LifecycleStatus == Contracts.Enums.TradeLifecycleStatus.Active ||
                t.LifecycleStatus == Contracts.Enums.TradeLifecycleStatus.PartialExit ||
                t.LifecycleStatus == Contracts.Enums.TradeLifecycleStatus.Closing)
            .OrderByDescending(t => t.EntryTime)
            .Take(count)
            .ToListAsync();

    public async Task AddAsync(Trade trade)
    {
        try
        {
            await _db.Trades.AddAsync(trade);
            await _db.SaveChangesAsync();
            _cache.AddOrUpdateTrade(trade); // O(1) Ram pointer tracking
            _riskAgent?.NotifyTradeUpdate(trade);
        }
        catch (DbUpdateException ex) when (IsDuplicateSignalInsert(ex))
        {
            throw new DuplicateSignalTradeException(trade.SignalId, ex);
        }
    }

    public async Task UpdateAsync(Trade trade)
    {
        _db.Trades.Update(trade);
        await _db.SaveChangesAsync();
        _cache.AddOrUpdateTrade(trade); // O(1) Ram pointer tracking
        _riskAgent?.NotifyTradeUpdate(trade);
    }

    private static bool IsDuplicateSignalInsert(DbUpdateException ex)
    {
        return ContainsDuplicateSignalIdMarker(ex.Message) ||
               (ex.InnerException is not null && ContainsDuplicateSignalIdMarker(ex.InnerException.Message));
    }

    private static bool ContainsDuplicateSignalIdMarker(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("SignalId", StringComparison.OrdinalIgnoreCase) &&
               (message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }
}
