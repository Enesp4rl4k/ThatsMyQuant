using System.Diagnostics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Writes a row to <c>ServiceHeartbeats</c> every ~30s so Listener.Api's
/// /api/system/health endpoint can report worker liveness.
/// </summary>
public sealed class ServiceHeartbeatHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ServiceHeartbeatHostedService> _logger;
    private readonly string _serviceName;
    private readonly string _version;
    private readonly string _machineName;
    private readonly int _processId;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    public ServiceHeartbeatHostedService(
        IServiceScopeFactory scopes,
        ILogger<ServiceHeartbeatHostedService> logger)
    {
        _scopes = scopes;
        _logger = logger;

        _serviceName = Truncate(
            Environment.GetEnvironmentVariable("TMQ_SERVICE_NAME")
                ?? Assembly.GetEntryAssembly()?.GetName().Name
                ?? "Unknown.Service",
            64);

        _version = Truncate(
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0",
            40);

        _machineName = Truncate(Environment.MachineName, 64);
        _processId = Environment.ProcessId;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First write immediately so the dashboard reflects boot quickly
        await WriteHeartbeatAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await WriteHeartbeatAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — flush a final "stopped" status
            await WriteHeartbeatAsync(CancellationToken.None, status: "stopped");
        }
    }

    private async Task WriteHeartbeatAsync(CancellationToken ct, string status = "running")
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
            var existing = await db.ServiceHeartbeats
                .FirstOrDefaultAsync(s => s.ServiceName == _serviceName, ct);

            var memoryBytes = Process.GetCurrentProcess().WorkingSet64;
            var now = DateTime.UtcNow;

            if (existing is null)
            {
                db.ServiceHeartbeats.Add(new ServiceHeartbeat
                {
                    ServiceName = _serviceName,
                    LastSeenUtc = now,
                    StartedUtc = _startedUtc,
                    Version = _version,
                    MachineName = _machineName,
                    ProcessId = _processId,
                    Status = status,
                    MemoryBytes = memoryBytes,
                });
            }
            else
            {
                existing.LastSeenUtc = now;
                existing.StartedUtc = _startedUtc;
                existing.Version = _version;
                existing.MachineName = _machineName;
                existing.ProcessId = _processId;
                existing.Status = status;
                existing.MemoryBytes = memoryBytes;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ServiceHeartbeat: failed to write row for {Service}.", _serviceName);
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max);
}
