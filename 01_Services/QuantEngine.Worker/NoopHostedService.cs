using Microsoft.Extensions.Hosting;

namespace ThatsMyQuant.Bot;

/// <summary>Placeholder when a venue does not support an optional hosted feature.</summary>
public sealed class NoopHostedService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
