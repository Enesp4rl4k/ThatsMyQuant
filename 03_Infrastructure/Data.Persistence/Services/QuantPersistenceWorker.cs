using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// HFT Bulk Execution Service preventing EF Core from blocking the trade evaluation threads.
/// Consumes signals from a non-blocking memory channel and writes them to the SQL store asynchronously.
/// </summary>
public class QuantPersistenceWorker : BackgroundService
{
    private const int MaxBatchSize = 128;
    private static readonly TimeSpan MaxBatchDelay = TimeSpan.FromMilliseconds(5);

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
        var batch = new List<Signal>(MaxBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var hasData = await _channel.WaitToReadAsync(stoppingToken);
                if (!hasData)
                {
                    break;
                }

                DrainBatch(batch);
                if (batch.Count < MaxBatchSize)
                {
                    await Task.Delay(MaxBatchDelay, stoppingToken);
                    DrainBatch(batch);
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

        DrainBatch(batch);
        if (batch.Count > 0)
        {
            await FlushBatchAsync(batch, CancellationToken.None);
        }
    }

    private async Task FlushBatchAsync(List<Signal> batch, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
        var latestBySignalId = new Dictionary<Guid, Signal>(batch.Count);
        foreach (var signal in batch)
        {
            latestBySignalId[signal.Id] = signal;
        }

        await SignalPersistenceBatchWriter.FlushAsync(db, latestBySignalId.Values.ToArray(), ct);
    }

    private void DrainBatch(List<Signal> batch)
    {
        while (batch.Count < MaxBatchSize && _channel.TryRead(out var signal))
        {
            batch.Add(signal);
        }
    }
}
