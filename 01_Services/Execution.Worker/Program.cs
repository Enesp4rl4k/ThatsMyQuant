using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Services;
using ThatsMyQuant.Infrastructure.Persistence;
using ThatsMyQuant.Infrastructure.Repositories;
using ThatsMyQuant.Execution.Worker;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Infrastructure;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Infrastructure.Ai;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ThatsMyQuant.Infrastructure.Services.Validators;
using ThatsMyQuant.Application.Common;

var builder = Host.CreateApplicationBuilder(args);

// ── Secret validation — fail before connecting to any exchange ─────────────
// Set secrets via: dotnet user-secrets set "<Key>" "<value>"
// (UserSecretsId is defined in Execution.Worker.csproj)
static void RequireSecret(IConfiguration cfg, string key, string hint)
{
    var value = cfg[key];
    if (string.IsNullOrWhiteSpace(value) || value.StartsWith("change-me", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            $"[Security] Required secret '{key}' is missing or still set to the placeholder default.\n" +
            $"  Fix: dotnet user-secrets set \"{key}\" \"{hint}\"");
}

var executionMode = builder.Configuration["Exchange:Mode"] ?? "Binance";

// Validate only the secrets needed for the configured exchange mode
switch (executionMode.ToLowerInvariant())
{
    case "okx":
        RequireSecret(builder.Configuration, "OKX:ApiKey",     "<your-okx-api-key>");
        RequireSecret(builder.Configuration, "OKX:ApiSecret",  "<your-okx-api-secret>");
        RequireSecret(builder.Configuration, "OKX:Passphrase", "<your-okx-passphrase>");
        break;
    case "both":
        RequireSecret(builder.Configuration, "Binance:ApiKey",    "<your-binance-api-key>");
        RequireSecret(builder.Configuration, "Binance:ApiSecret", "<your-binance-api-secret>");
        RequireSecret(builder.Configuration, "OKX:ApiKey",        "<your-okx-api-key>");
        RequireSecret(builder.Configuration, "OKX:ApiSecret",     "<your-okx-api-secret>");
        RequireSecret(builder.Configuration, "OKX:Passphrase",    "<your-okx-passphrase>");
        break;
    default: // "Binance"
        RequireSecret(builder.Configuration, "Binance:ApiKey",    "<your-binance-api-key>");
        RequireSecret(builder.Configuration, "Binance:ApiSecret", "<your-binance-api-secret>");
        break;
}
// ──────────────────────────────────────────────────────────────────────────

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("Yahoo", c => c.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0"));
builder.Services.AddHttpClient("Binance");
builder.Services.AddHttpClient("OKX");
var geminiApiKey = builder.Configuration["Ai:GeminiApiKey"];

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
builder.Logging.ClearProviders();
builder.Services.AddSerilog();

// --- CORE APPLICATION SERVICES ---
builder.Services.AddThatsMyQuantApplication(builder.Configuration);
builder.Services.AddThatsMyQuantInfrastructure();

// --- INFRASTRUCTURE ADAPTERS ---
builder.Services.AddSingleton<BorsaMarketDataService>();
builder.Services.AddSingleton<IMarketDataService, YahooFinanceMarketDataService>();
builder.Services.AddScoped<IReasoningProvider, DeepSeekReasoningService>();
builder.Services.AddScoped<IAiAnalystService, AiAnalystService>();
builder.Services.AddScoped<ISentimentService, GeminiSentimentService>();
builder.Services.AddScoped<ITechnicalAnalysisService, QuantIndicatorService>();
builder.Services.AddSingleton<BinanceExchangeInfoProvider>();
builder.Services.AddSingleton<IExchangeInfoProvider>(sp => sp.GetRequiredService<BinanceExchangeInfoProvider>());
builder.Services.AddSingleton<ITickLogger, JsonlTickLogger>();
// --- EXCHANGE STREAMING DATA SOURCE ---
// Mode: "Binance" (default), "Okx", or "Both" (dual-feed redundancy)
// executionMode is declared at the top of this file — same value drives both streaming and execution
switch (executionMode.ToLowerInvariant())
{
    case "okx":
        builder.Services.AddSingleton<IStreamingMarketDataService, OkxWebSocketService>();
        break;
    case "both":
        builder.Services.AddSingleton<BinanceWebSocketService>();
        builder.Services.AddSingleton<OkxWebSocketService>();
        builder.Services.AddSingleton<IStreamingMarketDataService>(sp =>
            new MultiExchangeStreamingService(
                sp.GetRequiredService<ILogger<MultiExchangeStreamingService>>(),
                new IStreamingMarketDataService[]
                {
                    sp.GetRequiredService<BinanceWebSocketService>(),
                    sp.GetRequiredService<OkxWebSocketService>()
                }));
        break;
    default: // "Binance"
        builder.Services.AddSingleton<IStreamingMarketDataService, BinanceWebSocketService>();
        break;
}

// Signal Validation Chain
builder.Services.AddScoped<ISignalValidator, MarketHoursValidator>();
builder.Services.AddScoped<ISignalValidator, PriceSanityValidator>();
builder.Services.AddScoped<ISignalValidator, MinVolumeValidator>();

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

builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ProcessSignalCommand).Assembly));

// Persistence — AddDbContextFactory registers both IDbContextFactory<T> (singleton-safe)
// and scoped DbContext; required so meta-learning singletons can open short-lived DB connections.
var quantDbConn = builder.Configuration.GetConnectionString("QuantDb") ?? "Data Source=quantbot.db;Cache=Shared";
builder.Services.AddDbContextFactory<QuantDbContext>(options =>
    options.UseSqlite(quantDbConn));
builder.Services.AddScoped<ISignalRepository, SignalRepository>();
builder.Services.AddSingleton<IPortfolioStateCache, PortfolioStateCache>();
builder.Services.AddScoped<ITradeRepository, TradeRepository>();

// Execution — REST order placement (mirrors the Exchange:Mode switch above)
switch (executionMode.ToLowerInvariant())
{
    case "okx":
        builder.Services.AddSingleton<IExchangeService, OkxExecutionService>();
        break;
    case "both":
        // "Both" routes execution to Binance by default.
        // Override with Exchange:ExecutionVenue=OKX to flip.
        var execVenue = builder.Configuration["Exchange:ExecutionVenue"] ?? "Binance";
        if (execVenue.Equals("OKX", StringComparison.OrdinalIgnoreCase))
            builder.Services.AddSingleton<IExchangeService, OkxExecutionService>();
        else
            builder.Services.AddSingleton<IExchangeService, BinanceExecutionService>();
        break;
    default: // "Binance"
        builder.Services.AddSingleton<IExchangeService, BinanceExecutionService>();
        break;
}
builder.Services.AddHostedService<ExecutionWorker>();
builder.Services.AddHostedService<ThatsMyQuant.Infrastructure.Services.HealthCheckHostedService>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
    await db.Database.EnsureCreatedAsync();

    var exchangeInfo = scope.ServiceProvider.GetRequiredService<BinanceExchangeInfoProvider>();
    await exchangeInfo.InitializeAsync();

    var portfolioCache = scope.ServiceProvider.GetRequiredService<IPortfolioStateCache>();
    await portfolioCache.InitializeAsync();
}

await host.RunAsync();
