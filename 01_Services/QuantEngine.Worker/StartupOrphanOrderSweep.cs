using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Cancels resting orders left over from the previous run before any strategy starts. A stop or
/// crash during the maker wait leaves a post_only order on the venue that nothing tracks; if it
/// fills later it moves the position behind the bot's back. Registered before the strategies:
/// hosts await StartAsync in registration order. Best effort: failing must not stop the bot.
/// </summary>
public sealed class StartupOrphanOrderSweep : IHostedService
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private readonly IExchangeService _exchange;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StartupOrphanOrderSweep> _logger;

    public StartupOrphanOrderSweep(IExchangeService exchange, IConfiguration configuration, ILogger<StartupOrphanOrderSweep> logger)
    {
        _exchange = exchange;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(_configuration["EdgeLabSingleAsset:HybridMode"], "live", StringComparison.OrdinalIgnoreCase))
            return;
        if (_exchange is not IOrphanOrderSweeper sweeper)
            return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Budget);
        try
        {
            var n = await sweeper.CancelOwnRestingOrdersAsync(cts.Token).ConfigureAwait(false);
            _logger.LogInformation("Startup orphan-order sweep done: {Count} order(s) cancelled.", n);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Startup orphan-order sweep failed; continuing.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
