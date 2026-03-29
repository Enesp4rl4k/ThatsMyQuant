using Microsoft.Extensions.DependencyInjection;
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

PerformanceBootstrapper.OptimizeForHft();

var builder = Host.CreateApplicationBuilder(args);

// ── Secret validation — fail before connecting to any exchange ─────────────
// Set secrets via: dotnet user-secrets set "<Key>" "<value>"
// (UserSecretsId is defined in ThatsMyQuant.Bot.csproj)
static void RequireSecret(IConfiguration cfg, string key, string hint)
{
    var value = cfg[key];
    if (string.IsNullOrWhiteSpace(value) || value.StartsWith("change-me", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            $"[Security] Required secret '{key}' is missing or still set to the placeholder default.\n" +
            $"  Fix: dotnet user-secrets set \"{key}\" \"{hint}\"");
}

RequireSecret(builder.Configuration, "Binance:ApiKey",    "<your-binance-api-key>");
RequireSecret(builder.Configuration, "Binance:ApiSecret", "<your-binance-api-secret>");
// ──────────────────────────────────────────────────────────────────────────

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("Yahoo", c => c.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0"));
builder.Services.AddHttpClient("Binance");
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
// --- EXCHANGE STREAMING DATA SOURCE ---
// Mode: "Binance" (default), "Okx", or "Both" (dual-feed redundancy)
var exchangeMode = builder.Configuration["Exchange:Mode"] ?? "Binance";
switch (exchangeMode.ToLowerInvariant())
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

// Persistence — AddDbContextFactory registers both IDbContextFactory<T> (singleton-safe)
// and scoped DbContext; required so meta-learning singletons can open short-lived DB connections.
var quantDbConn = builder.Configuration.GetConnectionString("QuantDb") ?? "Data Source=quantbot.db;Cache=Shared";
builder.Services.AddDbContextFactory<QuantDbContext>(options =>
    options.UseSqlite(quantDbConn));
builder.Services.AddScoped<ISignalRepository, SignalRepository>();
builder.Services.AddSingleton<IPortfolioStateCache, PortfolioStateCache>();
builder.Services.AddScoped<ITradeRepository, TradeRepository>();
builder.Services.AddScoped<ReconciliationAgent>();

// Execution — real Binance REST API
builder.Services.AddSingleton<IExchangeService, BinanceExecutionService>();

builder.Services.AddSingleton<ThatsMyQuant.Application.Security.ApiVault>();
builder.Services.AddSingleton<IHardwareSecurityModule, HardwareSecurityModule>();

builder.Services.AddTransient<ISignalValidator, MarketHoursValidator>();
builder.Services.AddTransient<ISignalValidator, MinVolumeValidator>();
builder.Services.AddTransient<ISignalValidator, PriceSanityValidator>();

builder.Services.AddHostedService<TradeBotWorker>();
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
