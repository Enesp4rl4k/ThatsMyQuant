using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

public sealed class ExchangeExecutionSyncHostedService : IHostedService, IObserver<ExecutionReport>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ExchangeExecutionSyncHostedService> _logger;
    private IExchangeExecutionSync? _executionSync;
    private IDisposable? _subscription;

    public ExchangeExecutionSyncHostedService(
        IServiceProvider serviceProvider,
        ILogger<ExchangeExecutionSyncHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _executionSync = _serviceProvider.GetService<IExchangeExecutionSync>();
        if (_executionSync is null)
        {
            _logger.LogInformation("Execution sync host disabled because no IExchangeExecutionSync implementation is registered.");
            return;
        }

        try
        {
            _subscription = _executionSync.ExecutionReports.Subscribe(this);
            await _executionSync.ConnectAsync(cancellationToken);
            _logger.LogInformation("Execution report synchronization connected.");
        }
        catch (Exception ex)
        {
            _subscription?.Dispose();
            _subscription = null;
            _logger.LogWarning(ex, "Execution report synchronization disabled because startup failed.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;

        if (_executionSync is not null)
        {
            await _executionSync.DisconnectAsync(cancellationToken);
            await _executionSync.DisposeAsync();
            _executionSync = null;
        }
    }

    public void OnCompleted()
        => _logger.LogInformation("Execution report synchronization completed.");

    public void OnError(Exception error)
        => _logger.LogError(error, "Execution report synchronization stream faulted.");

    public void OnNext(ExecutionReport value)
        => _ = ApplyExecutionReportAsync(value);

    private async Task ApplyExecutionReportAsync(ExecutionReport report)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var syncService = scope.ServiceProvider.GetRequiredService<TradeExecutionSyncService>();
            var applied = await syncService.ApplyExecutionReportAsync(report);

            _logger.LogDebug(
                "Execution report received for {Symbol}/{ClientOrderId}. Applied={Applied}, Status={Status}, Filled={FilledQuantity}, Remaining={RemainingQuantity}",
                report.Symbol,
                report.ClientOrderId,
                applied,
                report.Status,
                report.FilledQuantity,
                report.RemainingQuantity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply execution report for {Symbol}/{ClientOrderId}.", report.Symbol, report.ClientOrderId);
        }
    }
}
