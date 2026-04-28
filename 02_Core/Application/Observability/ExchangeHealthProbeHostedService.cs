using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.Observability;

/// <summary>
/// Periodically pings the active <see cref="IExchangeService"/> by calling
/// <c>GetAccountStatusAsync</c> to verify credentials are valid and the venue
/// is reachable. Updates <see cref="IExchangeHealthProvider"/>; the Dashboard
/// polls the resulting <c>/exchange/health</c> endpoint to render a status
/// badge and warn the operator if credentials are inactive.
/// </summary>
public sealed class ExchangeHealthProbeHostedService : BackgroundService
{
    private readonly ILogger<ExchangeHealthProbeHostedService> _logger;
    private readonly IServiceProvider _services;
    private readonly IExchangeHealthProvider _health;
    private readonly TimeSpan _interval;
    private readonly string _exchangeMode;
    private readonly bool _isDemoMode;
    private readonly bool _hasOkxCredentials;

    public ExchangeHealthProbeHostedService(
        ILogger<ExchangeHealthProbeHostedService> logger,
        IServiceProvider services,
        IExchangeHealthProvider health,
        IConfiguration configuration)
    {
        _logger = logger;
        _services = services;
        _health = health;

        var seconds = 30;
        if (int.TryParse(configuration["ExchangeHealth:IntervalSeconds"], out var s) && s >= 10)
            seconds = s;
        _interval = TimeSpan.FromSeconds(seconds);

        _exchangeMode = (configuration["TMQ_EXCHANGE_MODE"] ?? "Mock").Trim();
        _isDemoMode = bool.TryParse(configuration["OKX:IsDemoMode"], out var demo) && demo;
        _hasOkxCredentials =
            !string.IsNullOrWhiteSpace(configuration["OKX:ApiKey"]) &&
            !string.IsNullOrWhiteSpace(configuration["OKX:SecretKey"]) &&
            !string.IsNullOrWhiteSpace(configuration["OKX:Passphrase"]);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_hasOkxCredentials && _exchangeMode.StartsWith("OKX", StringComparison.OrdinalIgnoreCase))
        {
            _health.MarkCredentialsMissing(_exchangeMode);
            _logger.LogWarning("OKX credentials not configured; exchange health probe will report INACTIVE.");
            return;
        }

        _logger.LogInformation(
            "ExchangeHealthProbeHostedService started: venue={Venue} demo={Demo} interval={Interval}s.",
            _exchangeMode, _isDemoMode, _interval.TotalSeconds);

        // First probe immediately so the Dashboard doesn't sit on stale "not probed" text.
        await ProbeOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) { break; }

            await ProbeOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProbeOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var exchange = scope.ServiceProvider.GetService<IExchangeService>();
            if (exchange is null)
            {
                _health.RecordFailure(_exchangeMode, _isDemoMode, "IExchangeService not registered.");
                return;
            }

            var account = await exchange.GetAccountStatusAsync().WaitAsync(ct).ConfigureAwait(false);
            if (account is null)
            {
                _health.RecordFailure(_exchangeMode, _isDemoMode, "GetAccountStatus returned null.");
                return;
            }

            _health.RecordSuccess(_exchangeMode, _isDemoMode, account.Balance);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _health.RecordFailure(_exchangeMode, _isDemoMode, ex.Message);
            _logger.LogWarning(ex, "Exchange health probe failed for {Venue}.", _exchangeMode);
        }
    }
}
