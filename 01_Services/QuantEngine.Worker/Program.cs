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

// --- CORE APPLICATION SERVICES ---
builder.Services.AddThatsMyQuantInfrastructure();
builder.Services.AddThatsMyQuantApplication();
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
// Exchange-aware streaming: OKX or Binance based on TMQ_EXCHANGE_MODE
if (isOkxMode)
{
    builder.Services.AddSingleton<IStreamingMarketDataService, OkxWebSocketService>();
}
else
{
    builder.Services.AddSingleton<IStreamingMarketDataService, BinanceWebSocketService>();
}
builder.Services.AddSingleton<BinanceExchangeInfoProvider>();
builder.Services.AddSingleton<IExchangeInfoProvider>(sp => sp.GetRequiredService<BinanceExchangeInfoProvider>());
builder.Services.AddSingleton<ITickLogger, JsonlTickLogger>();

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

var okxDemoFlag = builder.Configuration["OKX:IsDemoMode"] ?? builder.Configuration["OKX__IsDemoMode"];
var isOkxDemo = bool.TryParse(okxDemoFlag, out var okxDemo) && okxDemo;
var allowLiveTradingFlag = builder.Configuration["TMQ_ALLOW_LIVE_TRADING"];
var allowLiveTrading = bool.TryParse(allowLiveTradingFlag, out var allowLive) && allowLive;
var credentialSource = StartupSafetyPolicy.ResolveCredentialSource(hasOkxCredentials, hasBinanceCredentials);
var executionSyncEnabled = isOkxMode && hasOkxCredentials;

Log.Information(
    "QuantEngine.Worker startup: ExchangeMode={ExchangeMode} | OKX_Creds={HasOkxCreds} (Demo={OkxDemo}) | Binance_Creds={HasBinanceCreds} (BaseUrl={BinanceBaseUrl}) | LiveOverride={AllowLiveTrading} | CredentialSource={CredentialSource} | ExecutionSync={ExecutionSyncEnabled}",
    selectedExchangeMode,
    hasOkxCredentials,
    isOkxDemo,
    hasBinanceCredentials,
    binanceBaseUrl,
    allowLiveTrading,
    credentialSource,
    executionSyncEnabled);

StartupSafetyPolicy.EnforceOrThrow(
    "QuantEngine.Worker",
    selectedExchangeMode,
    isMockMode,
    isOkxMode,
    isOkxDemo,
    allowLiveTrading,
    hasOkxCredentials,
    hasBinanceCredentials);

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
            sp.GetRequiredService<ISharedPortfolioStateManager>(),
            sp.GetService<ThatsMyQuant.Application.Security.ApiVault>(),
            sp.GetService<OperationalTelemetry>(),
            sp.GetRequiredService<IStreamingMarketDataService>()));
    builder.Services.AddSingleton<IExchangeExecutionSync>(sp =>
        new OkxExecutionSync(
            sp.GetRequiredService<ILogger<OkxExecutionSync>>(),
            builder.Configuration,
            sp.GetService<ThatsMyQuant.Application.Security.ApiVault>()));

    Log.Information("QuantEngine.Worker: OKX execution service registered (Demo={OkxDemo}).", isOkxDemo);
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
            sp.GetRequiredService<ISharedPortfolioStateManager>(),
            sp.GetService<OperationalTelemetry>());
    });

    Log.Information("QuantEngine.Worker: Binance execution service registered in {Mode} mode.", selectedExchangeMode);
}
else
{
    builder.Services.AddSingleton<IExchangeService>(sp => 
    {
        var inner = new MockBrokerService(sp.GetRequiredService<ILogger<MockBrokerService>>()); 
        return new ChaosExchangeDecorator(inner, sp.GetRequiredService<ILogger<ChaosExchangeDecorator>>());
    });

    Log.Warning("QuantEngine.Worker: Mock exchange execution registered (Chaos decorator enabled).");
}

builder.Services.AddSingleton<IVenueTradeFeeProvider>(sp =>
{
    var ex = sp.GetRequiredService<IExchangeService>();
    return ex as IVenueTradeFeeProvider ?? new NullVenueTradeFeeProvider();
});
builder.Services.Replace(ServiceDescriptor.Singleton<IExecutionCostSnapshotCache, ExecutionCostSnapshotCache>());

builder.Services.TryAddSingleton<ThatsMyQuant.Application.Security.ApiVault>();
builder.Services.AddSingleton<IHardwareSecurityModule, HardwareSecurityModule>();

builder.Services.AddTransient<ISignalValidator, MarketHoursValidator>();
builder.Services.AddTransient<ISignalValidator, MinVolumeValidator>();
builder.Services.AddTransient<ISignalValidator, PriceSanityValidator>();

builder.Services.AddHostedService<WorkerManagementApiHostedService>();
builder.Services.AddHostedService<ExchangeExecutionSyncHostedService>();

// TMQ_MODE dispatch: paper | live (default). `backtest` runs via the CLI tool instead.
var runMode = (builder.Configuration["TMQ_MODE"] ?? "live").ToLowerInvariant();
switch (runMode)
{
    case "paper":
        builder.Services.AddHostedService<PaperTradingHostedService>();
        Log.Information("QuantEngine.Worker: TMQ_MODE=paper. PaperTradingHostedService registered.");
        break;

    case "live":
    default:
        builder.Services.AddHostedService<TradeBotWorker>();
        if (bool.TryParse(builder.Configuration["QuantEngine:LiveSignalScan:Enabled"], out var liveScan) && liveScan)
        {
            var symbols = builder.Configuration["QuantEngine:LiveSignalScan:Symbols"] ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(symbols))
            {
                builder.Services.AddHostedService<LiveSignalScanHostedService>();
                Log.Information("QuantEngine.Worker: LiveSignalScanHostedService registered (symbols: {Symbols}).", symbols);
            }
            else
            {
                Log.Warning("QuantEngine.Worker: LiveSignalScan:Enabled=true but QuantEngine:LiveSignalScan:Symbols is empty; scan service not registered.");
            }
        }

        Log.Information(
            "QuantEngine.Worker: TMQ_MODE={Mode}. TradeBotWorker registered. HFT/tick signals are produced in Execution.Worker (TickPipeline); optional candle scan runs here when configured.",
            runMode);
        break;
}

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

    var promotion = scope.ServiceProvider.GetRequiredService<StrategyPromotionService>();
    await promotion.InitializeAsync();
}

host.Services.GetRequiredService<ServiceRuntimeState>().MarkReady();

await host.RunAsync();
