using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Domain.Interfaces;

public interface ITradeRepository
{
    Task<Trade?> GetByIdAsync(Guid id);
    Task<Trade?> GetBySignalIdAsync(Guid signalId);
    Task<IReadOnlyList<Trade>> GetAllAsync(int count = 100);
    Task AddAsync(Trade trade);
    Task UpdateAsync(Trade trade);
}
