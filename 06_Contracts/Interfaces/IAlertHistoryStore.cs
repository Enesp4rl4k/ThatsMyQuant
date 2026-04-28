using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface IAlertHistoryStore
{
    Task<int> RecordAsync(AlertHistoryEntry entry, CancellationToken ct = default);
    Task UpdateStatusAsync(int id, AlertDispatchStatus status, string? failureReason = null, CancellationToken ct = default);
    Task<IReadOnlyList<AlertHistoryEntry>> GetRecentAsync(int limit = 100, AlertKind? kindFilter = null, CancellationToken ct = default);
}
