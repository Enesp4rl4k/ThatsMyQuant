using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Commands;

namespace Listener.Api.Services;

/// <summary>
/// A native .NET BackgroundWorker processing the Signal arrays populated by Webhook requests.
/// Allows the HTTP layer to achieve 202 Accepted return times under 1 millisecond.
/// </summary>
public class SignalIngestionWorker : BackgroundService
{
    private readonly SignalIngestionChannel _channel;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SignalIngestionWorker> _logger;

    public SignalIngestionWorker(
        SignalIngestionChannel channel, 
        IServiceProvider serviceProvider, 
        ILogger<SignalIngestionWorker> logger)
    {
        _channel = channel;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var signal in _channel.ReadAllAsync(stoppingToken))
        {
            try
            {
                // Execution scope per-signal isolating the Context memory graph
                using var scope = _serviceProvider.CreateScope();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                // Swarm Logic Execution
                await mediator.Send(new ProcessSignalCommand(signal), stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Catastrophic failure in the AI Swarm Event Processing Loop.");
            }
        }
    }
}
