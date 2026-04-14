using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.HftInfrastructure;

public sealed class HeartbeatWatchdogHostedService : BackgroundService
{
    private readonly HeartbeatMonitor _heartbeatMonitor;
    private readonly ILogger<HeartbeatWatchdogHostedService> _logger;

    public HeartbeatWatchdogHostedService(
        HeartbeatMonitor heartbeatMonitor,
        ILogger<HeartbeatWatchdogHostedService> logger)
    {
        _heartbeatMonitor = heartbeatMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                _heartbeatMonitor.AuditConnection();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Heartbeat watchdog audit loop failed.");
            }
        }
    }
}
