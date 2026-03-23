using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// HFT Bulk Execution Service preventing EF Core from blocking the trade evaluation threads.
/// Consumes signals from a non-blocking memory channel and writes them to the SQL store asynchronously.
/// </summary>
public class QuantPersistenceWorker : BackgroundService
{
    private readonly SignalWriterChannel _channel;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<QuantPersistenceWorker> _logger;

    public QuantPersistenceWorker(
        SignalWriterChannel channel,
        IServiceProvider serviceProvider,
        ILogger<QuantPersistenceWorker> logger)
    {
        _channel = channel;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Limit EF Core parameterized batch inserts to 1000 parameters to respect TCP Frame geometries
        var batch = new List<Signal>(1000);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Await lock-free until the memory array notifies us of incoming elements
                var hasData = await _channel.WaitToReadAsync(stoppingToken);
                if (!hasData) break;

                // Sync-drain the array locally (up to 1,000 array elements immediately)
                while (batch.Count < 1000 && _channel.TryRead(out var signal))
                {
                    batch.Add(signal);
                }

                if (batch.Count > 0)
                {
                    await FlushBatchAsync(batch, stoppingToken);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Catastrophic failure in the Data Persistence Worker.");
                await Task.Delay(1000, stoppingToken); // Fallback heartbeat sleep to prevent crash-looping
            }
        }
    }

    private async Task FlushBatchAsync(List<Signal> batch, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();

        await db.Signals.AddRangeAsync(batch, ct);
        await db.SaveChangesAsync(ct);
        
        // _logger.LogInformation($"[PERSISTENCE] Successfully bulk-inserted {batch.Count} Signals out of the main execution thread.");
    }
}
