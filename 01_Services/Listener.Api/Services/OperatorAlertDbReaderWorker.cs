using MediatR;
using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Contracts.Notifications;
using ThatsMyQuant.Infrastructure.Persistence;

namespace Listener.Api.Services;

/// <summary>
/// Polls the cross-process <c>OperatorAlertEvents</c> table every 3 seconds and
/// republishes new rows via MediatR. RealtimeBroadcaster then fans them out to
/// the SignalR <c>OperatorAlert</c> channel.
///
/// Watermark: tracks the last-seen <c>Id</c> in memory; on startup we initialise
/// to the current MAX(Id) so the dashboard does not flood with replays of old
/// alerts. Trim policy: rows older than 30 days are deleted on each tick.
/// </summary>
public sealed class OperatorAlertDbReaderWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OperatorAlertDbReaderWorker> _logger;
    private long _lastSeenId;
    private DateTime _lastTrimUtc = DateTime.MinValue;

    public OperatorAlertDbReaderWorker(
        IServiceScopeFactory scopes,
        ILogger<OperatorAlertDbReaderWorker> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
            _lastSeenId = await db.OperatorAlertEvents.MaxAsync(e => (long?)e.Id, stoppingToken) ?? 0L;
            _logger.LogInformation("OperatorAlertDbReaderWorker initialised at watermark Id={Id}.", _lastSeenId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OperatorAlertDbReaderWorker: failed to read initial watermark — starting at 0.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "OperatorAlertDbReaderWorker tick failed.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var watermark = _lastSeenId;
        var newRows = await db.OperatorAlertEvents
            .Where(e => e.Id > watermark)
            .OrderBy(e => e.Id)
            .Take(50)
            .ToListAsync(ct);

        foreach (var e in newRows)
        {
            await mediator.Publish(
                new OperatorAlertNotification(e.Severity, e.Source, e.Title, e.Body, e.UtcTime),
                ct);
            _lastSeenId = e.Id;
        }

        // Trim old rows once an hour.
        if ((DateTime.UtcNow - _lastTrimUtc) > TimeSpan.FromHours(1))
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            var deleted = await db.OperatorAlertEvents
                .Where(e => e.UtcTime < cutoff)
                .ExecuteDeleteAsync(ct);
            if (deleted > 0)
            {
                _logger.LogInformation("OperatorAlertDbReaderWorker trimmed {Deleted} alert(s) older than 30 days.", deleted);
            }
            _lastTrimUtc = DateTime.UtcNow;
        }
    }
}
