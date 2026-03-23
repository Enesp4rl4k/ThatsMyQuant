using Listener.Api.Middleware;
using MediatR;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using ThatsMyQuant.Application.Commands;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Infrastructure.Persistence;
using ThatsMyQuant.Infrastructure.Repositories;
using ThatsMyQuant.Infrastructure.Services;
using ThatsMyQuant.Application.Common;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Infrastructure.Ai;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

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
builder.Services.AddDbContext<QuantDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("QuantDb") ?? "Data Source=C:\\Users\\parlak\\Downloads\\ThatsMyQuant\\quant.db"));

builder.Services.AddSingleton<ThatsMyQuant.Infrastructure.Services.SignalWriterChannel>();
builder.Services.AddHostedService<ThatsMyQuant.Infrastructure.Services.QuantPersistenceWorker>();
builder.Services.AddSingleton<IPortfolioStateCache, ThatsMyQuant.Application.Strategy.PortfolioStateCache>();
builder.Services.AddScoped<ISignalRepository, SignalRepository>();
builder.Services.AddScoped<ITradeRepository, TradeRepository>();

builder.Services.AddSingleton<Listener.Api.Services.SignalIngestionChannel>();
builder.Services.AddHostedService<Listener.Api.Services.SignalIngestionWorker>();

// --- MediatR ---
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ProcessSignalCommand).Assembly));

// --- HttpClient ---
builder.Services.AddHttpClient<YahooFinanceMarketDataService>(client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
    client.Timeout = TimeSpan.FromSeconds(10);
});

// --- CORE APPLICATION SERVICES ---
builder.Services.AddThatsMyQuantApplication();

// --- INFRASTRUCTURE ADAPTERS ---
builder.Services.AddSingleton<BorsaMarketDataService>();
builder.Services.AddSingleton<IMarketDataService, YahooFinanceMarketDataService>();
builder.Services.AddSingleton<IAiReasoningService, DeepSeekReasoningService>();
builder.Services.AddSingleton<IAiAnalystService, AiAnalystService>();
builder.Services.AddSingleton<ISentimentService, GeminiSentimentService>();
builder.Services.AddSingleton<ITechnicalAnalysisService, QuantIndicatorService>();
builder.Services.AddSingleton<IExchangeService, MockBrokerService>();

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
    db.Database.EnsureCreated();
    scope.ServiceProvider.GetRequiredService<IPortfolioStateCache>().InitializeAsync().Wait();
}

app.UseMiddleware<HmacValidationMiddleware>();
app.UseMiddleware<ApiKeyValidationMiddleware>();

// 🚀 HFT PIPELINE: Decouple Listener Network Thread
app.MapPost("/webhook", (Signal signal, Listener.Api.Services.SignalIngestionChannel channel) =>
{
    // O(1) Push into active RAM memory block
    channel.TryWrite(signal);

    // Reply cleanly to executing TradingView/Remote Webhooks securely under 1 millisecond limits
    return Results.Accepted();
});

app.Run();
