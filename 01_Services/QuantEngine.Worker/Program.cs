using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Serilog;
using Microsoft.SemanticKernel;
using ThatsMyQuant.Bot;
using ThatsMyQuant.Application.Security;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Services;
using ThatsMyQuant.Infrastructure.Ai;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Infrastructure.Repositories;
using ThatsMyQuant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using MediatR;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Application.Common;
using ThatsMyQuant.Infrastructure;
using ThatsMyQuant.Infrastructure.Services.Validators;
using ThatsMyQuant.Application.Observability;

PerformanceBootstrapper.OptimizeForHft();

var builder = Host.CreateApplicationBuilder(args);
var selectedExchangeMode = (builder.Configuration["TMQ_EXCHANGE_MODE"] ?? "Mock").Trim();
var isBinanceMode =
    selectedExchangeMode.Equals("Binance", StringComparison.OrdinalIgnoreCase) ||
    selectedExchangeMode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase);
var isOkxMode = selectedExchangeMode.Equals("OKX", StringComparison.OrdinalIgnoreCase);
var binanceBaseUrl = selectedExchangeMode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase)
    ? "https://testnet.binance.vision"
    : "https://api.binance.com";

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("Yahoo", c => c.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0"));
builder.Services.AddHttpClient("Binance", c =>
{
    c.BaseAddress = new Uri(binanceBaseUrl);
});
builder.Services.AddHttpClient("OKX");
var geminiApiKey = builder.Configuration["Ai:GeminiApiKey"];

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] ({CorrelationId}) {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
builder.Logging.ClearProviders();
builder.Services.AddSerilog();

// --- CORE APPLICATION SERVICES ---
builder.Services.AddThatsMyQuantApplication();
builder.Services.AddThatsMyQuantInfrastructure();
builder.Services.AddSingleton(new ServiceRuntimeState("QuantEngine.Worker"));
builder.Services.AddSingleton(new OperationalTelemetry("QuantEngine.Worker"));

// --- INFRASTRUCTURE ADAPTERS ---
builder.Services.AddSingleton<BorsaMarketDataService>();
builder.Services.AddSingleton<YahooFinanceMarketDataService>();
builder.Services.AddSingleton<IMarketDataService, ExchangeRoutedMarketDataService>();
builder.Services.AddScoped<IReasoningProvider, DeepSeekReasoningService>();
builder.Services.AddScoped<IAiAnalystService, AiAnalystService>();
builder.Services.AddScoped<ISentimentService, GeminiSentimentService>();
builder.Services.AddScoped<ITechnicalAnalysisService, QuantIndicatorService>();
builder.Services.AddSingleton<IStreamingMarketDataService, BinanceWebSocketService>();
builder.Services.AddSingleton<BinanceExchangeInfoProvider>();
builder.Services.AddSingleton<IExchangeInfoProvider>(sp => sp.GetRequiredService<BinanceExchangeInfoProvider>());

// Safety Kernel
if (!string.IsNullOrWhiteSpace(geminiApiKey))
{
    builder.Services.AddKernel().AddGoogleAIGeminiChatCompletion("gemini-pro", geminiApiKey);
}
else
{
    builder.Services.AddKernel(); 
    builder.Services.AddSingleton<IChatCompletionService, MockChatCompletionService>();
}

// MediatR
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ProcessSignalCommand).Assembly));

// Persistence
builder.Services.AddDbContextPool<QuantDbContext>(options =>
    options.UseConfiguredQuantDatabase(builder.Configuration), poolSize: 128);
builder.Services.AddSingleton<SignalWriterChannel>();
builder.Services.AddHostedService<QuantPersistenceWorker>();
builder.Services.AddScoped<ISignalRepository, SignalRepository>();
builder.Services.AddSingleton<IPortfolioStateCache, PortfolioStateCache>();
builder.Services.AddScoped<ITradeRepository, TradeRepository>();
builder.Services.AddScoped<ReconciliationAgent>();

var hasOkxCredentials =
    !string.IsNullOrWhiteSpace(builder.Configuration["OKX:ApiKey"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["OKX:Passphrase"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["OKX:SecretKey"]);
var hasBinanceCredentials =
    !string.IsNullOrWhiteSpace(builder.Configuration["TMQ_BINANCE_API_KEY"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["TMQ_BINANCE_API_SECRET"]);

// Execution routing follows the launcher-selected venue.
if (isOkxMode && hasOkxCredentials)
{
    builder.Services.AddSingleton<ThatsMyQuant.Application.Security.ApiVault>(sp =>
    {
        var vault = new ThatsMyQuant.Application.Security.ApiVault();
        vault.SecureInboundKey(builder.Configuration["OKX:SecretKey"]!);
        return vault;
    });
    builder.Services.AddSingleton<IExchangeService>(sp =>
        new OkxExecutionService(
            sp.GetRequiredService<ILogger<OkxExecutionService>>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("OKX"),
            builder.Configuration,
            sp.GetService<ISharedPortfolioStateManager>(),
            sp.GetService<ThatsMyQuant.Application.Security.ApiVault>()));
}
else if (isBinanceMode && hasBinanceCredentials)
{
    builder.Services.AddSingleton<IExchangeService>(sp =>
    {
        var keyVault = new ApiVault();
        var secretVault = new ApiVault();
        keyVault.SecureInboundKey(builder.Configuration["TMQ_BINANCE_API_KEY"]!);
        secretVault.SecureInboundKey(builder.Configuration["TMQ_BINANCE_API_SECRET"]!);

        return new BinanceExecutionService(
            sp.GetRequiredService<ILogger<BinanceExecutionService>>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("Binance"),
            keyVault,
            secretVault,
            sp.GetRequiredService<IStreamingMarketDataService>(),
            sp.GetService<ISharedPortfolioStateManager>());
    });
}
else
{
    builder.Services.AddSingleton<IExchangeService>(sp => 
    {
        var inner = new MockBrokerService(sp.GetRequiredService<ILogger<MockBrokerService>>()); 
        return new ChaosExchangeDecorator(inner, sp.GetRequiredService<ILogger<ChaosExchangeDecorator>>());
    });
}

builder.Services.TryAddSingleton<ThatsMyQuant.Application.Security.ApiVault>();
builder.Services.AddSingleton<IHardwareSecurityModule, HardwareSecurityModule>();

builder.Services.AddTransient<ISignalValidator, MarketHoursValidator>();
builder.Services.AddTransient<ISignalValidator, MinVolumeValidator>();
builder.Services.AddTransient<ISignalValidator, PriceSanityValidator>();

builder.Services.AddHostedService<WorkerManagementApiHostedService>();
builder.Services.AddHostedService<TradeBotWorker>();

var host = builder.Build();
using (var scope = host.Services.CreateScope())
{
    var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
    var startupLogger = loggerFactory.CreateLogger("QuantEngine.Worker.Startup");
    var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
    await SqliteCompatibilityBootstrapper.EnsureCompatibleSchemaAsync(db, builder.Configuration, startupLogger);

    var exchangeInfo = scope.ServiceProvider.GetRequiredService<BinanceExchangeInfoProvider>();
    try
    {
        await exchangeInfo.InitializeAsync();
    }
    catch (Exception ex)
    {
        startupLogger.LogWarning(ex, "Binance exchange metadata initialization failed. QuantEngine.Worker will continue with degraded symbol metadata.");
    }

    var portfolioCache = scope.ServiceProvider.GetRequiredService<IPortfolioStateCache>();
    await portfolioCache.InitializeAsync();
}

host.Services.GetRequiredService<ServiceRuntimeState>().MarkReady();

await host.RunAsync();
