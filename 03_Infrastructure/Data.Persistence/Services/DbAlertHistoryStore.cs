using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Infrastructure.Services;

public class DbAlertHistoryStore : IAlertHistoryStore
{
    private readonly IDbContextFactory<QuantDbContext> _factory;
    private readonly ILogger<DbAlertHistoryStore> _logger;

    public DbAlertHistoryStore(IDbContextFactory<QuantDbContext> factory, ILogger<DbAlertHistoryStore> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<int> RecordAsync(AlertHistoryEntry entry, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            db.AlertHistory.Add(entry);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return entry.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AlertHistory record failed for {Symbol}/{Kind}", entry.Symbol, entry.Kind);
            return 0;
        }
    }

    public async Task UpdateStatusAsync(int id, AlertDispatchStatus status, string? failureReason = null, CancellationToken ct = default)
    {
        if (id <= 0) return;
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.AlertHistory.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
            if (row is null) return;
            row.Status = status;
            if (!string.IsNullOrWhiteSpace(failureReason))
                row.FailureReason = failureReason.Length > 500 ? failureReason[..500] : failureReason;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AlertHistory status update failed for id {Id}", id);
        }
    }

    public async Task<IReadOnlyList<AlertHistoryEntry>> GetRecentAsync(int limit = 100, AlertKind? kindFilter = null, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            IQueryable<AlertHistoryEntry> q = db.AlertHistory.AsNoTracking();
            if (kindFilter.HasValue)
                q = q.Where(x => x.Kind == kindFilter.Value);
            return await q.OrderByDescending(x => x.TimestampUtc)
                .Take(Math.Clamp(limit, 1, 1000))
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AlertHistory query failed");
            return Array.Empty<AlertHistoryEntry>();
        }
    }
}
