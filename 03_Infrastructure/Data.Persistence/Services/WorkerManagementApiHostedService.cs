using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Observability;

namespace ThatsMyQuant.Infrastructure.Services;

public sealed class WorkerManagementApiHostedService : IHostedService
{
    private readonly IServiceProvider _rootProvider;
    private readonly IConfiguration _configuration;
    private readonly ServiceRuntimeState _runtimeState;
    private readonly OperationalTelemetry _telemetry;
    private readonly ILogger<WorkerManagementApiHostedService> _logger;
    private WebApplication? _app;

    public WorkerManagementApiHostedService(
        IServiceProvider rootProvider,
        IConfiguration configuration,
        ServiceRuntimeState runtimeState,
        OperationalTelemetry telemetry,
        ILogger<WorkerManagementApiHostedService> logger)
    {
        _rootProvider = rootProvider;
        _configuration = configuration;
        _runtimeState = runtimeState;
        _telemetry = telemetry;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var managementUrl = _configuration["Management:Url"];
        if (string.IsNullOrWhiteSpace(managementUrl))
        {
            _logger.LogInformation("Management API disabled for {ServiceName} because Management:Url is not configured.", _runtimeState.ServiceName);
            return;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(managementUrl);
        builder.Services.AddSingleton(_runtimeState);
        builder.Services.AddSingleton(_telemetry);

        _app = builder.Build();
        _app.MapObservabilityEndpoints(_rootProvider);
        await _app.StartAsync(cancellationToken);

        _logger.LogInformation("Management API for {ServiceName} listening on {Url}", _runtimeState.ServiceName, managementUrl);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is null)
        {
            return;
        }

        await _app.StopAsync(cancellationToken);
        await _app.DisposeAsync();
        _app = null;
    }
}
