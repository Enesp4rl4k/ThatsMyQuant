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
using Listener.Api.Hubs;
using Listener.Api.Services;

PerformanceBootstrapper.OptimizeForHft();

var builder = WebApplication.CreateBuilder(args);

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
var connectionString = builder.Configuration.GetConnectionString("QuantDb") ?? "Data Source=quant.db;Cache=Shared";
builder.Services.AddDbContextPool<QuantDbContext>(options =>
    options.UseSqlite(connectionString), poolSize: 128);

builder.Services.AddSingleton<SignalWriterChannel>();
builder.Services.AddSingleton<SignalIngestionChannel>();
builder.Services.AddHostedService<SignalIngestionWorker>();
builder.Services.AddHostedService<QuantPersistenceWorker>();
builder.Services.AddScoped<ISignalRepository, SignalRepository>();
builder.Services.AddSingleton<IPortfolioStateCache, PortfolioStateCache>();
builder.Services.AddScoped<ITradeRepository, TradeRepository>();

// --- MediatR ---
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ProcessSignalCommand).Assembly));

// --- Shared infrastructure (HTTP clients, market data, validators, exchange info) ---
builder.Services.AddThatsMyQuantCommonInfrastructure(builder.Configuration);

// --- AI / Reasoning (host-specific due to SemanticKernel dependency) ---
builder.Services.AddScoped<IReasoningProvider, DeepSeekReasoningService>();
builder.Services.AddScoped<IAiAnalystService, AiAnalystService>();
builder.Services.AddScoped<ISentimentService, GeminiSentimentService>();
builder.Services.AddScoped<ITechnicalAnalysisService, QuantIndicatorService>();

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

// --- Listener-specific services ---
builder.Services.AddSingleton<IExchangeService, MockBrokerService>();
builder.Services.AddSingleton<ITickLogger, JsonlTickLogger>();

builder.Services.AddHostedService<WebhookCleanupService>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<MarketDataBroadcastService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MarketDataBroadcastService>());

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

// --- Startup Secret Validation ---
// Fail fast if critical secrets are not configured (use dotnet user-secrets or env vars)
var webhookSecret = builder.Configuration["WebhookSecret"];
var internalApiKey = builder.Configuration["Security:InternalApiKey"];

if (string.IsNullOrWhiteSpace(webhookSecret))
{
    throw new InvalidOperationException(
        "WebhookSecret is not configured. Use 'dotnet user-secrets set WebhookSecret <value>' " +
        "or set the WEBHOOKSECRET environment variable.");
}

if (string.IsNullOrWhiteSpace(internalApiKey))
{
    throw new InvalidOperationException(
        "Security:InternalApiKey is not configured. Use 'dotnet user-secrets set Security:InternalApiKey <value>' " +
        "or set the SECURITY__INTERNALAPIKEY environment variable.");
}

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
app.UseCors("AllowReact");

app.MapPost("/webhook", (Signal signal, SignalIngestionChannel channel) =>
{
    // Fire into the bounded channel — the SignalIngestionWorker processes it asynchronously.
    // Returns 202 in < 1ms, shielding HTTP from heavy AI swarm processing.
    return channel.TryWrite(signal) ? Results.Accepted() : Results.StatusCode(503);
});

app.MapHub<MarketDataHub>("/market-hub");

await app.RunAsync();
