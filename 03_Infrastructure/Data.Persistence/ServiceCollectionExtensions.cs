using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ThatsMyQuant.Application.Common;
using ThatsMyQuant.Application.Security;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Services;
using ThatsMyQuant.Infrastructure.Services.Validators;

namespace ThatsMyQuant.Infrastructure;

/// <summary>
/// Shared DI composition used by all worker services and the dashboard.
/// Eliminates duplicated registration across Listener.Api, Execution.Worker,
/// QuantEngine.Worker, and Dashboard.
///
/// Note: Semantic Kernel and AI provider registration (which depends on external packages
/// like Microsoft.SemanticKernel.Connectors.Google) must be done per-host since the
/// Infrastructure project does not reference those packages.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the common infrastructure stack: HTTP clients, market data, AI services,
    /// signal validators, and exchange info.
    /// </summary>
    public static IServiceCollection AddThatsMyQuantCommonInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // --- HTTP Clients ---
        services.AddHttpClient();
        services.AddHttpClient("Yahoo", client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddHttpClient("Binance");

        // --- Core Application + Infrastructure DI ---
        services.AddThatsMyQuantApplication();
        services.AddThatsMyQuantInfrastructure();

        // --- Market Data ---
        services.AddSingleton<BorsaMarketDataService>();
        services.AddSingleton<IMarketDataService, YahooFinanceMarketDataService>();

        // --- Security ---
        services.AddSingleton<ApiVault>();
        services.AddSingleton<IHardwareSecurityModule, HardwareSecurityModule>();

        // --- Exchange Info ---
        services.AddSingleton<BinanceExchangeInfoProvider>();
        services.AddSingleton<IExchangeInfoProvider>(sp => sp.GetRequiredService<BinanceExchangeInfoProvider>());

        // --- Streaming Market Data ---
        services.AddSingleton<IStreamingMarketDataService, BinanceWebSocketService>();

        // --- Signal Validation Chain ---
        services.AddScoped<ISignalValidator, MarketHoursValidator>();
        services.AddScoped<ISignalValidator, PriceSanityValidator>();
        services.AddScoped<ISignalValidator, MinVolumeValidator>();

        return services;
    }
}
