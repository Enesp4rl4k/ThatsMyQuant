using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Infrastructure.Services;

public class DbAuditLogger : IAuditLogger
{
    private readonly IDbContextFactory<QuantDbContext> _dbFactory;
    private readonly ILogger<DbAuditLogger> _logger;

    public DbAuditLogger(IDbContextFactory<QuantDbContext> dbFactory, ILogger<DbAuditLogger> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task LogOrderExecutionAsync(string orderId, string strategyId, string justification)
    {
        var payload = JsonSerializer.Serialize(new { orderId, strategyId, justification });
        await WriteEntryAsync(new AuditEntry
        {
            Id = Guid.NewGuid(),
            EventType = "OrderExecution",
            CorrelationId = orderId,
            TimestampUtc = DateTime.UtcNow,
            Payload = payload
        });
    }

    public async Task LogSecurityEventAsync(string principalId, string role, string actionType, string details)
    {
        var payload = JsonSerializer.Serialize(new { principalId, role, actionType, details });
        await WriteEntryAsync(new AuditEntry
        {
            Id = Guid.NewGuid(),
            EventType = "SecurityEvent",
            CorrelationId = principalId,
            TimestampUtc = DateTime.UtcNow,
            Payload = payload
        });
    }

    public async Task<List<AuditEntry>> QueryByCorrelationIdAsync(string correlationId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.AuditLog
            .Where(a => a.CorrelationId == correlationId)
            .OrderBy(a => a.TimestampUtc)
            .ToListAsync();
    }

    public async Task<List<AuditEntry>> QueryByTimeRangeAsync(DateTime from, DateTime to)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.AuditLog
            .Where(a => a.TimestampUtc >= from && a.TimestampUtc <= to)
            .OrderBy(a => a.TimestampUtc)
            .ToListAsync();
    }

    private async Task WriteEntryAsync(AuditEntry entry)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.AuditLog.Add(entry);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit entry EventType={EventType} CorrelationId={CorrelationId}",
                entry.EventType, entry.CorrelationId);
            throw;
        }
    }
}
