using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface ISignalRepository
{
    Task<Signal?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<Signal>> GetRecentAsync(int count = 50);
    Task<IReadOnlyList<Signal>> GetByStatusAsync(TradeStatus status, int count = 50);
    Task AddAsync(Signal signal);
    Task UpdateAsync(Signal signal);
    Task<Dictionary<TradeStatus, int>> GetStatusCountsAsync();
}
