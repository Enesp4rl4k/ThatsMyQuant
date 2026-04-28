using Listener.Api.Middleware;
using MediatR;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Threading.RateLimiting;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Persistence;
using ThatsMyQuant.Infrastructure.Repositories;
using ThatsMyQuant.Application.Security;
using ThatsMyQuant.Infrastructure.Services;
using ThatsMyQuant.Application.Common;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Infrastructure;
using ThatsMyQuant.Infrastructure.Ai;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ThatsMyQuant.Infrastructure.Services.Validators;
using Listener.Api.Auth;
using Listener.Api.Endpoints;
using Listener.Api.Hubs;
using Listener.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Serilog;
using ThatsMyQuant.Application.Observability;

PerformanceBootstrapper.OptimizeForHft();

var builder = WebApplication.CreateBuilder(args);
// Runtime overrides — operators tune a small whitelist via PATCH /api/settings/runtime.
// Reload-on-change so IOptionsMonitor<T> consumers pick up changes without restart.
builder.Configuration.AddJsonFile(
    Path.Combine(builder.Environment.ContentRootPath, "appsettings.runtime.json"),
    optional: true,
    reloadOnChange: true);
builder.Configuration.ApplySharedExchangeConfiguration();
builder.Configuration.ApplySharedTelegramConfiguration();
var selectedExchangeMode = SharedExchangeConfiguration.GetSelectedExchangeMode(builder.Configuration);
var isOkxMode =
    selectedExchangeMode.Equals("OKX", StringComparison.OrdinalIgnoreCase) ||
    selectedExchangeMode.Equals("OKXSwap", StringComparison.OrdinalIgnoreCase);
var isBinanceMode =
    selectedExchangeMode.Equals("Binance", StringComparison.OrdinalIgnoreCase) ||
    selectedExchangeMode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase) ||
    selectedExchangeMode.Equals("BinanceFutures", StringComparison.OrdinalIgnoreCase) ||
    selectedExchangeMode.Equals("BinanceFuturesTestnet", StringComparison.OrdinalIgnoreCase);
var binanceBaseUrl = selectedExchangeMode switch
{
    var mode when mode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase) => "https://testnet.binance.vision",
    var mode when mode.Equals("BinanceFutures", StringComparison.OrdinalIgnoreCase) => "https://fapi.binance.com",
    var mode when mode.Equals("BinanceFuturesTestnet", StringComparison.OrdinalIgnoreCase) => "https://testnet.binancefuture.com",
    _ => "https://api.binance.com"
};

var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThatsMyQuant", "logs");
Directory.CreateDirectory(logDir);
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "Listener.Api")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] ({CorrelationId}) {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        new Serilog.Formatting.Compact.CompactJsonFormatter(),
        Path.Combine(logDir, "listener-.clef"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 50L * 1024 * 1024,
        rollOnFileSizeLimit: true,
        shared: true)
    .CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("webhook", limiterOptions =>
    {
        limiterOptions.PermitLimit = 30;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
        limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});

// --- Database ---
builder.Services.AddDbContextPool<QuantDbContext>(options =>
    options.UseConfiguredQuantDatabase(builder.Configuration), poolSize: 128);

builder.Services.AddSingleton<SignalWriterChannel>();
builder.Services.AddHostedService<QuantPersistenceWorker>();
builder.Services.AddScoped<ISignalRepository, SignalRepository>();
builder.Services.AddSingleton<IPortfolioStateCache, PortfolioStateCache>();
builder.Services.AddScoped<ITradeRepository, TradeRepository>();

// --- MediatR ---
// Register MediatR handlers from BOTH the Contracts assembly (for command/notification types)
// and the Application assembly (where ProcessSignalHandler / SwarmNegotiationRequest /
// CircuitBreakerFlattenHandler etc. live). Without the second registration, any endpoint
// that does mediator.Send(new ProcessSignalCommand(...)) — webhook receivers, manual order
// entry, signal ingestion worker — gets "No service for IRequestHandler<>" at runtime.
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(ProcessSignalCommand).Assembly);
    cfg.RegisterServicesFromAssembly(typeof(ThatsMyQuant.Application.Handlers.ProcessSignalHandler).Assembly);
});

// --- HttpClient ---
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("Yahoo", client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient("Binance", client =>
{
    client.BaseAddress = new Uri(binanceBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient("OKX");
builder.Services.AddHttpClient("OkxPublic", client =>
{
    client.BaseAddress = new Uri("https://www.okx.com");
    client.Timeout = TimeSpan.FromSeconds(10);
});

// --- CORE APPLICATION SERVICES ---
builder.Services.AddThatsMyQuantInfrastructure();
builder.Services.AddThatsMyQuantApplication();
builder.Services.AddSingleton(new ServiceRuntimeState("Listener.Api"));
builder.Services.AddSingleton(new OperationalTelemetry("Listener.Api"));

// --- INFRASTRUCTURE ADAPTERS ---
builder.Services.AddSingleton<BorsaMarketDataService>();
builder.Services.AddSingleton<YahooFinanceMarketDataService>();
builder.Services.AddSingleton<IMarketDataService, ExchangeRoutedMarketDataService>();
builder.Services.AddScoped<IReasoningProvider, DeepSeekReasoningService>();
builder.Services.AddScoped<IAiAnalystService, AiAnalystService>();
builder.Services.AddScoped<ISentimentService, GeminiSentimentService>();
builder.Services.AddScoped<ITechnicalAnalysisService, QuantIndicatorService>();
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
    "Listener.Api startup: ExchangeMode={ExchangeMode} | OKX_Creds={HasOkxCreds} (Demo={OkxDemo}) | Binance_Creds={HasBinanceCreds} (BaseUrl={BinanceBaseUrl}) | LiveOverride={AllowLiveTrading} | CredentialSource={CredentialSource} | ExecutionSync={ExecutionSyncEnabled}",
    selectedExchangeMode,
    hasOkxCredentials,
    isOkxDemo,
    hasBinanceCredentials,
    binanceBaseUrl,
    allowLiveTrading,
    credentialSource,
    executionSyncEnabled);

// Listener.Api only receives webhooks and forwards them via MediatR —
// it never places orders itself, so exchange credentials are optional here.
// The safety policy still blocks LIVE mode without the explicit acknowledgement flag.
StartupSafetyPolicy.EnforceOrThrow(
    "Listener.Api",
    selectedExchangeMode,
    selectedExchangeMode.Equals("Mock", StringComparison.OrdinalIgnoreCase),
    isOkxMode,
    isOkxDemo,
    allowLiveTrading,
    hasOkxCredentials,
    hasBinanceCredentials,
    requiresTradingCredentials: false);

if (isOkxMode && hasOkxCredentials)
{
    builder.Services.AddSingleton<ApiVault>(sp =>
    {
        var vault = new ApiVault();
        vault.SecureInboundKey(builder.Configuration["OKX:SecretKey"] ?? "");
        return vault;
    });
    builder.Services.AddSingleton<IExchangeService>(sp =>
        new OkxExecutionService(
            sp.GetRequiredService<ILogger<OkxExecutionService>>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("OKX"),
            builder.Configuration,
            sp.GetRequiredService<ISharedPortfolioStateManager>(),
            sp.GetRequiredService<ApiVault>(),
            sp.GetService<OperationalTelemetry>(),
            sp.GetRequiredService<IStreamingMarketDataService>()));

    Log.Information("Listener.Api: OKX execution service registered (Demo={OkxDemo}).", isOkxDemo);
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

    Log.Information("Listener.Api: Binance execution service registered in {Mode} mode.", selectedExchangeMode);
}
else
{
    builder.Services.AddSingleton<IExchangeService, MockBrokerService>();

    Log.Warning("Listener.Api: Mock exchange execution registered.");
}

builder.Services.AddSingleton<IVenueTradeFeeProvider>(sp =>
{
    var ex = sp.GetRequiredService<IExchangeService>();
    return ex as IVenueTradeFeeProvider ?? new NullVenueTradeFeeProvider();
});

// IExchangePositionReader bridges /api/paper/exchange-positions to OKX's
// /api/v5/account/positions (which IExchangeService.GetAccountStatusAsync doesn't surface).
// Worker registers the same way; this mirrors it so Listener.Api can show real OKX swap positions.
builder.Services.AddSingleton<IExchangePositionReader>(sp =>
{
    var ex = sp.GetRequiredService<IExchangeService>();
    if (ex is IExchangePositionReader reader) return reader;
    Log.Warning("Listener.Api: IExchangeService does not implement IExchangePositionReader — exchange-positions endpoint will return empty.");
    return new Listener.Api.NullExchangePositionReader();
});
builder.Services.Replace(ServiceDescriptor.Singleton<IExecutionCostSnapshotCache, ExecutionCostSnapshotCache>());

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
builder.Services.TryAddSingleton<ThatsMyQuant.Application.Security.ApiVault>();
builder.Services.AddSingleton<IHardwareSecurityModule, HardwareSecurityModule>();
builder.Services.AddSingleton<BinanceExchangeInfoProvider>();
builder.Services.AddSingleton<IExchangeInfoProvider>(sp => sp.GetRequiredService<BinanceExchangeInfoProvider>());

// Signal Validation Chain
builder.Services.AddScoped<ISignalValidator, MarketHoursValidator>();
builder.Services.AddScoped<ISignalValidator, PriceSanityValidator>();
builder.Services.AddScoped<ISignalValidator, MinVolumeValidator>();

// Safety Kernel
var geminiApiKey = builder.Configuration["Ai:GeminiApiKey"];
if (!string.IsNullOrWhiteSpace(geminiApiKey))
{
    builder.Services.AddKernel().AddGoogleAIGeminiChatCompletion("gemini-pro", geminiApiKey);
}
else
{
    builder.Services.AddKernel(); 
    builder.Services.AddSingleton<IChatCompletionService, MockChatCompletionService>();
}

builder.Services.AddHostedService<WebhookCleanupService>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<MarketDataBroadcastService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MarketDataBroadcastService>());
// Real-time L2 order book via OKX public WS (books5 channel).
// Registered only in OKX mode — falls back to synthetic REST polling otherwise.
if (isOkxMode)
{
    builder.Services.AddSingleton<OkxOrderBookSubscriber>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OkxOrderBookSubscriber>());
}
builder.Services.AddHostedService<PortfolioBroadcastWorker>();
builder.Services.AddSingleton<WritableSettingsStore>();
builder.Services.AddHostedService<OperatorAlertBridgeWorker>();
builder.Services.AddSingleton<ThatsMyQuant.Contracts.Interfaces.IOperatorAlertPublisher, MediatorOperatorAlertPublisher>();
builder.Services.AddHostedService<OperatorAlertDbReaderWorker>();
builder.Services.AddHostedService<ThatsMyQuant.Infrastructure.Services.ServiceHeartbeatHostedService>();
builder.Services.AddTransient<MediatR.INotificationHandler<ThatsMyQuant.Contracts.Notifications.PositionOpenedNotification>, RealtimeBroadcaster>();
builder.Services.AddTransient<MediatR.INotificationHandler<ThatsMyQuant.Contracts.Notifications.PositionClosedNotification>, RealtimeBroadcaster>();
builder.Services.AddTransient<MediatR.INotificationHandler<ThatsMyQuant.Application.Strategy.ProMode.CircuitBreakerTrippedNotification>, RealtimeBroadcaster>();
builder.Services.AddTransient<MediatR.INotificationHandler<ThatsMyQuant.Contracts.Notifications.OperatorAlertNotification>, RealtimeBroadcaster>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReact", policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://localhost:3000") // Vite/CRA defaults
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Phase 14 — JWT auth (off by default; opt-in via Auth:Enabled = true).
// When disabled, [Authorize] is treated as anonymous so existing X-Api-Key flow keeps working.
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
if (string.IsNullOrWhiteSpace(authOptions.JwtSecret))
{
    // Generate a per-install secret and persist it back to appsettings.runtime.json so subsequent
    // restarts keep the same key (otherwise everyone's tokens would invalidate on every boot).
    authOptions.JwtSecret = JwtTokenService.GenerateSecret();
    var runtimeJsonPath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.runtime.json");
    try
    {
        var existing = File.Exists(runtimeJsonPath)
            ? System.Text.Json.JsonDocument.Parse(File.ReadAllText(runtimeJsonPath)).RootElement
            : default;
        var dict = existing.ValueKind == System.Text.Json.JsonValueKind.Object
            ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(existing.GetRawText())
                ?? new Dictionary<string, object>()
            : new Dictionary<string, object>();
        dict["Auth"] = new { authOptions.Enabled, authOptions.JwtSecret, authOptions.Issuer, authOptions.Audience, authOptions.TokenLifetimeHours };
        File.WriteAllText(runtimeJsonPath,
            System.Text.Json.JsonSerializer.Serialize(dict, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        builder.Configuration.AddJsonFile(runtimeJsonPath, optional: true, reloadOnChange: true);
    }
    catch
    {
        // Fall through — secret stays in-memory, tokens invalidate on restart but the app still works.
    }
}
builder.Services.AddSingleton<JwtTokenService>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = authOptions.Issuer,
            ValidAudience = authOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authOptions.JwtSecret)),
            ClockSkew = TimeSpan.FromMinutes(2),
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseRateLimiter();
app.UseCors("AllowReact");
app.UseMiddleware<CorrelationIdMiddleware>();

// Database Migration
using (var scope = app.Services.CreateScope())
{
    var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
    var startupLogger = loggerFactory.CreateLogger("Listener.Api.Startup");
    var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
    await SqliteCompatibilityBootstrapper.EnsureCompatibleSchemaAsync(db, builder.Configuration, startupLogger);

    var exchangeInfo = scope.ServiceProvider.GetRequiredService<BinanceExchangeInfoProvider>();
    try
    {
        await exchangeInfo.InitializeAsync();
    }
    catch (Exception ex)
    {
        startupLogger.LogWarning(ex, "Binance exchange metadata initialization failed. Listener.Api will continue with degraded symbol metadata.");
    }

    var portfolioCache = scope.ServiceProvider.GetRequiredService<IPortfolioStateCache>();
    await portfolioCache.InitializeAsync();

    var promotion = scope.ServiceProvider.GetRequiredService<StrategyPromotionService>();
    await promotion.InitializeAsync();
}

app.Services.GetRequiredService<ServiceRuntimeState>().MarkReady();

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<HmacValidationMiddleware>();
app.UseMiddleware<ApiKeyValidationMiddleware>();

app.MapObservabilityEndpoints(app.Services);
app.MapDashboardEndpoints();
app.MapProModeEndpoints();
app.MapFundingEndpoints();
app.MapPairsEndpoints();
app.MapStrategyEndpoints();
app.MapPaperStateEndpoints();
app.MapLiveSignalEndpoints();
app.MapQuantLabEndpoints();
app.MapAlphaPortfolioEndpoints();
app.MapSettingsEndpoints();
app.MapAiEndpoints();
app.MapPromotionEndpoints();
app.MapAuditEndpoints();
app.MapSystemHealthEndpoints();
app.MapAuthEndpoints();
app.MapMarketsEndpoints();
app.MapDashboardIntelEndpoints();

app.MapPost("/webhook", async (Signal signal, HttpContext context, IMediator mediator) =>
{
    if (!signal.Metadata.ContainsKey("CorrelationId") &&
        context.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var correlationId) &&
        correlationId is string correlationValue &&
        !string.IsNullOrWhiteSpace(correlationValue))
    {
        signal.Metadata["CorrelationId"] = correlationValue;
    }

    await mediator.Send(new ProcessSignalCommand(signal));
    return Results.Accepted();
});

app.MapHub<MarketDataHub>("/market-hub");

await app.RunAsync();
