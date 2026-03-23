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
    options.UseSqlite(builder.Configuration.GetConnectionString("QuantDb") ?? "Data Source=quant.db"));

builder.Services.AddScoped<ISignalRepository, SignalRepository>();
builder.Services.AddScoped<ITradeRepository, TradeRepository>();

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
    await db.Database.EnsureCreatedAsync();
}

app.UseMiddleware<HmacValidationMiddleware>();
app.UseMiddleware<ApiKeyValidationMiddleware>();

app.MapPost("/webhook", async (Signal signal, IMediator mediator) =>
{
    await mediator.Send(new ProcessSignalCommand(signal));
    return Results.Accepted();
});

await app.RunAsync();
