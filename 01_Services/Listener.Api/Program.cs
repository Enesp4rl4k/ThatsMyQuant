using Listener.Api.Middleware;
using MediatR;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
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

PerformanceBootstrapper.OptimizeForHft();

var builder = WebApplication.CreateBuilder(args);

// ── Secret validation — fail before accepting any traffic ──────────────────
// Set secrets via: dotnet user-secrets set "<Key>" "<value>"
// (UserSecretsId is defined in Listener.Api.csproj)
static void RequireSecret(IConfiguration cfg, string key, string hint)
{
    var value = cfg[key];
    if (string.IsNullOrWhiteSpace(value) || value.StartsWith("change-me", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            $"[Security] Required secret '{key}' is missing or still set to the placeholder default.\n" +
            $"  Fix: dotnet user-secrets set \"{key}\" \"{hint}\"");
}

RequireSecret(builder.Configuration, "WebhookSecret",          "<64-char-random-hex>");
RequireSecret(builder.Configuration, "Security:InternalApiKey", "<32-char-random-hex>");
// ──────────────────────────────────────────────────────────────────────────

builder.Services.AddMemoryCache();
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
var quantDbConn = builder.Configuration.GetConnectionString("QuantDb") ?? "Data Source=quantbot.db;Cache=Shared";
builder.Services.AddDbContext<QuantDbContext>(options =>
    options.UseSqlite(quantDbConn));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<QuantDbContext>();
builder.Services.AddScoped<ISignalRepository, SignalRepository>();
builder.Services.AddSingleton<IPortfolioStateCache, PortfolioStateCache>();
builder.Services.AddScoped<ITradeRepository, TradeRepository>();

// --- MediatR ---
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ProcessSignalCommand).Assembly));

// --- HttpClient ---
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("Yahoo", client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient("Binance");

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
builder.Services.AddSingleton<IExchangeService, MockBrokerService>();
builder.Services.AddSingleton<ITickLogger, JsonlTickLogger>();
builder.Services.AddSingleton<IStreamingMarketDataService, BinanceWebSocketService>();
builder.Services.AddSingleton<ThatsMyQuant.Application.Security.ApiVault>();
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

var app = builder.Build();

app.UseHttpsRedirection();
app.UseRateLimiter();

// Database Migration
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
    await db.Database.EnsureCreatedAsync();

    var exchangeInfo = scope.ServiceProvider.GetRequiredService<BinanceExchangeInfoProvider>();
    await exchangeInfo.InitializeAsync();
}

app.UseMiddleware<HmacValidationMiddleware>();
app.UseMiddleware<ApiKeyValidationMiddleware>();

app.MapHealthChecks("/health");

app.MapPost("/webhook", async (Signal signal, IMediator mediator) =>
{
    await mediator.Send(new ProcessSignalCommand(signal));
    return Results.Accepted();
});

await app.RunAsync();
