using Microsoft.Extensions.DependencyInjection;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Services;

namespace ThatsMyQuant.Infrastructure;

/// <summary>
/// Resolves the appropriate IExchangeService implementation based on exchange name.
/// Used by ExecutionWorker to route orders to the correct exchange.
/// </summary>
public sealed class ExchangeServiceResolver
{
    private readonly IServiceProvider _serviceProvider;

    public ExchangeServiceResolver(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Resolves exchange service by name. Falls back to the default IExchangeService registration.
    /// </summary>
    public IExchangeService Resolve(string exchangeName)
    {
        return exchangeName?.ToUpperInvariant() switch
        {
            "OKX" => _serviceProvider.GetService<OkxExecutionService>()
                      ?? _serviceProvider.GetRequiredService<IExchangeService>(),
            "BINANCE" => _serviceProvider.GetService<BinanceExecutionService>()
                         ?? _serviceProvider.GetRequiredService<IExchangeService>(),
            _ => _serviceProvider.GetRequiredService<IExchangeService>()
        };
    }
}
