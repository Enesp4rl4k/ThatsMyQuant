using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Infrastructure.Services;

public class WebhookCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WebhookCleanupService> _logger;

    public WebhookCleanupService(IServiceProvider serviceProvider, ILogger<WebhookCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WebhookCleanupService starting (cleaning expired idempotency records)...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();

                var now = DateTime.UtcNow;
                var expiredLogs = await db.WebhookRequestLogs
                    .Where(x => x.ExpiresAtUtc < now)
                    .ExecuteDeleteAsync(stoppingToken);

                if (expiredLogs > 0)
                {
                    _logger.LogInformation("Deleted {Count} expired webhook idempotency records.", expiredLogs);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while cleaning expired webhook logs.");
            }

            // Run once per hour
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
