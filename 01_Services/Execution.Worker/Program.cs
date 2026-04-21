using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Serilog;
using ThatsMyQuant.Application.Common;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.Observability;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure;
using ThatsMyQuant.Infrastructure.Ai;
using ThatsMyQuant.Infrastructure.Persistence;
using ThatsMyQuant.Infrastructure.Repositories;
using ThatsMyQuant.Infrastructure.Services;
using ThatsMyQuant.Infrastructure.Services.Validators;
using ThatsMyQuant.Execution.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("exchange_settings.json", optional: true, reloadOnChange: true);
builder.Configuration.ApplySharedExchangeConfiguration();
var selectedExchangeMode = SharedExchangeConfiguration.GetSelectedExchangeMode(builder.Configuration);
var isBinanceMode =
    selectedExchangeMode.Equals("Binance", StringComparison.OrdinalIgnoreCase) ||
    selectedExchangeMode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase) ||
    selectedExchangeMode.Equals("BinanceFutures", StringComparison.OrdinalIgnoreCase) ||
    selectedExchangeMode.Equals("BinanceFuturesTestnet", StringComparison.OrdinalIgnoreCase);
var isOkxMode =
    selectedExchangeMode.Equals("OKX", StringComparison.OrdinalIgnoreCase) ||
    selectedExchangeMode.Equals("OKXSwap", StringComparison.OrdinalIgnoreCase);
var isMockMode = selectedExchangeMode.Equals("Mock", StringComparison.OrdinalIgnoreCase);
var binanceBaseUrl = selectedExchangeMode switch
{
    var mode when mode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase) => "https://testnet.binance.vision",
    var mode when mode.Equals("BinanceFutures", StringComparison.OrdinalIgnoreCase) => "https://fapi.binance.com",
    var mode when mode.Equals("BinanceFuturesTestnet", StringComparison.OrdinalIgnoreCase) => "https://testnet.binancefuture.com",
    _ => "https://api.binance.com"
};

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("Yahoo", c => c.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0"));
builder.Services.AddHttpClient("Binance", c =>
{
    c.BaseAddress = new Uri(binanceBaseUrl);
});
builder.Services.AddHttpClient("OKX");
builder.Services.AddHttpClient("OkxPublic", c =>
{
    c.BaseAddress = new Uri("https://www.okx.com");
    c.Timeout = TimeSpan.FromSeconds(10);
});

var geminiApiKey = builder.Configuration["Ai:GeminiApiKey"];

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] ({CorrelationId}) {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
builder.Logging.ClearProviders();
builder.Services.AddSerilog();

builder.Services.AddThatsMyQuantInfrastructure();
builder.Services.AddThatsMyQuantApplication();
builder.Services.AddSingleton(new ServiceRuntimeState("Execution.Worker"));
builder.Services.AddSingleton(new OperationalTelemetry("Execution.Worker"));

builder.Services.AddSingleton<BorsaMarketDataService>();
builder.Services.AddSingleton<YahooFinanceMarketDataService>();
builder.Services.AddSingleton<IMarketDataService, ExchangeRoutedMarketDataService>();
builder.Services.AddScoped<IReasoningProvider, DeepSeekReasoningService>();
builder.Services.AddScoped<IAiAnalystService, AiAnalystService>();
builder.Services.AddScoped<ISentimentService, GeminiSentimentService>();
builder.Services.AddScoped<ITechnicalAnalysisService, QuantIndicatorService>();
builder.Services.AddSingleton<BinanceExchangeInfoProvider>();
builder.Services.AddSingleton<IExchangeInfoProvider>(sp => sp.GetRequiredService<BinanceExchangeInfoProvider>());
builder.Services.AddSingleton<ITickLogger, JsonlTickLogger>();
// Exchange-aware streaming: OKX or Binance based on TMQ_EXCHANGE_MODE
if (isOkxMode)
{
    builder.Services.AddSingleton<IStreamingMarketDataService, OkxWebSocketService>();
}
else
{
    builder.Services.AddSingleton<IStreamingMarketDataService, BinanceWebSocketService>();
}
builder.Services.AddSingleton(sp =>
{
    var vault = new ThatsMyQuant.Application.Security.ApiVault();
    var okxSecret = builder.Configuration["OKX:SecretKey"];
    if (!string.IsNullOrWhiteSpace(okxSecret))
    {
        vault.SecureInboundKey(okxSecret);
    }

    return vault;
});

builder.Services.AddScoped<ISignalValidator, MarketHoursValidator>();
builder.Services.AddScoped<ISignalValidator, PriceSanityValidator>();
builder.Services.AddScoped<ISignalValidator, MinVolumeValidator>();

if (!string.IsNullOrWhiteSpace(geminiApiKey))
{
    builder.Services.AddKernel().AddGoogleAIGeminiChatCompletion("gemini-pro", geminiApiKey);
}
else
{
    builder.Services.AddKernel();
    builder.Services.AddSingleton<IChatCompletionService, MockChatCompletionService>();
}

builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ProcessSignalCommand).Assembly));

builder.Services.AddDbContextPool<QuantDbContext>(options =>
    options.UseConfiguredQuantDatabase(builder.Configuration), poolSize: 128);
builder.Services.AddSingleton<SignalWriterChannel>();
builder.Services.AddHostedService<QuantPersistenceWorker>();
builder.Services.AddScoped<ISignalRepository, SignalRepository>();
builder.Services.AddSingleton<IPortfolioStateCache, PortfolioStateCache>();
builder.Services.AddScoped<ITradeRepository, TradeRepository>();

var hasOkxCredentials =
    !string.IsNullOrWhiteSpace(builder.Configuration["OKX:ApiKey"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["OKX:Passphrase"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["OKX:SecretKey"]);

var hasBinanceCredentials =
    !string.IsNullOrWhiteSpace(builder.Configuration["TMQ_BINANCE_API_KEY"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["TMQ_BINANCE_API_SECRET"]);

var okxDemoFlag = builder.Configuration["OKX:IsDemoMode"] ?? builder.Configuration["OKX__IsDemoMode"];
var isOkxDemo = bool.TryParse(okxDemoFlag, out var okxDemo) && okxDemo;
var allowLiveTradingFlag = builder.Configuration["TMQ_ALLOW_LIVE_TRADING"];
var allowLiveTrading = bool.TryParse(allowLiveTradingFlag, out var allowLive) && allowLive;
var credentialSource = StartupSafetyPolicy.ResolveCredentialSource(hasOkxCredentials, hasBinanceCredentials);
var executionSyncEnabled = isOkxMode && hasOkxCredentials;

Log.Information(
    "Execution.Worker startup: ExchangeMode={ExchangeMode} | OKX_Creds={HasOkxCreds} (Demo={OkxDemo}) | Binance_Creds={HasBinanceCreds} (BaseUrl={BinanceBaseUrl}) | LiveOverride={AllowLiveTrading} | CredentialSource={CredentialSource} | ExecutionSync={ExecutionSyncEnabled}",
    selectedExchangeMode,
    hasOkxCredentials,
    isOkxDemo,
    hasBinanceCredentials,
    binanceBaseUrl,
    allowLiveTrading,
    credentialSource,
    executionSyncEnabled);

StartupSafetyPolicy.EnforceOrThrow(
    "Execution.Worker",
    selectedExchangeMode,
    isMockMode,
    isOkxMode,
    isOkxDemo,
    allowLiveTrading,
    hasOkxCredentials,
    hasBinanceCredentials);

if (isOkxMode && hasOkxCredentials)
{
    builder.Services.AddSingleton<OkxExecutionService>(sp =>
    {
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("OKX");
        var apiVault = sp.GetService<ThatsMyQuant.Application.Security.ApiVault>();
        return new OkxExecutionService(
            sp.GetRequiredService<ILogger<OkxExecutionService>>(),
            httpClient,
            builder.Configuration,
            sp.GetRequiredService<ISharedPortfolioStateManager>(),
            apiVault,
            sp.GetService<OperationalTelemetry>(),
            sp.GetRequiredService<IStreamingMarketDataService>());
    });

    builder.Services.AddSingleton<IExchangeService>(sp => sp.GetRequiredService<OkxExecutionService>());
    builder.Services.AddSingleton<IExchangeExecutionSync>(sp =>
        new OkxExecutionSync(
            sp.GetRequiredService<ILogger<OkxExecutionSync>>(),
            builder.Configuration,
            sp.GetService<ThatsMyQuant.Application.Security.ApiVault>()));
    Log.Information("Execution.Worker: OKX execution service registered (Demo={OkxDemo}).", isOkxDemo);
}
else if (isBinanceMode && hasBinanceCredentials)
{
    builder.Services.AddSingleton<BinanceExecutionService>(sp =>
    {
        var keyVault = new ThatsMyQuant.Application.Security.ApiVault();
        var secretVault = new ThatsMyQuant.Application.Security.ApiVault();
        keyVault.SecureInboundKey(builder.Configuration["TMQ_BINANCE_API_KEY"]!);
        secretVault.SecureInboundKey(builder.Configuration["TMQ_BINANCE_API_SECRET"]!);

        return new BinanceExecutionService(
            sp.GetRequiredService<ILogger<BinanceExecutionService>>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("Binance"),
            keyVault,
            secretVault,
            sp.GetRequiredService<IStreamingMarketDataService>(),
            sp.GetRequiredService<ISharedPortfolioStateManager>(),
            sp.GetService<OperationalTelemetry>());
    });
    builder.Services.AddSingleton<IExchangeService>(sp => sp.GetRequiredService<BinanceExecutionService>());
    Log.Information("Binance execution service registered in {Mode} mode.", selectedExchangeMode);
}
else
{
    builder.Services.AddSingleton<MockBrokerService>();
    builder.Services.AddSingleton<IExchangeService>(sp => sp.GetRequiredService<MockBrokerService>());
    Log.Warning(
        "{ExchangeMode} credentials were not found or the mode is unsupported. Execution.Worker is running in mock execution mode.",
        selectedExchangeMode);
}

builder.Services.AddSingleton<IVenueTradeFeeProvider>(sp =>
{
    var ex = sp.GetRequiredService<IExchangeService>();
    return ex as IVenueTradeFeeProvider ?? new NullVenueTradeFeeProvider();
});
builder.Services.Replace(ServiceDescriptor.Singleton<IExecutionCostSnapshotCache, ExecutionCostSnapshotCache>());

builder.Services.AddHostedService<WorkerManagementApiHostedService>();
builder.Services.AddHostedService<ExchangeExecutionSyncHostedService>();
builder.Services.AddHostedService<ExecutionWorker>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
    var startupLogger = loggerFactory.CreateLogger("Execution.Worker.Startup");
    var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
    var connString = db.Database.GetDbConnection().ConnectionString;
    startupLogger.LogInformation("DATABASE: Using connection string: {ConnectionString}", connString);
    await SqliteCompatibilityBootstrapper.EnsureCompatibleSchemaAsync(db, builder.Configuration, startupLogger);

    var exchangeInfo = scope.ServiceProvider.GetRequiredService<BinanceExchangeInfoProvider>();
    try
    {
        await exchangeInfo.InitializeAsync();
    }
    catch (Exception ex)
    {
        startupLogger.LogWarning(ex, "Binance exchange metadata initialization failed. Execution.Worker will continue with degraded symbol metadata.");
    }

    var portfolioCache = scope.ServiceProvider.GetRequiredService<IPortfolioStateCache>();
    await portfolioCache.InitializeAsync();

    var promotion = scope.ServiceProvider.GetRequiredService<StrategyPromotionService>();
    await promotion.InitializeAsync();
}

host.Services.GetRequiredService<ServiceRuntimeState>().MarkReady();

await host.RunAsync();
