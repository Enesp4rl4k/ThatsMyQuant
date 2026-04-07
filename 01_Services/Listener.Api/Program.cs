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
using Serilog;
using ThatsMyQuant.Application.Observability;

PerformanceBootstrapper.OptimizeForHft();

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] ({CorrelationId}) {Message:lj}{NewLine}{Exception}")
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
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ProcessSignalCommand).Assembly));

// --- HttpClient ---
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("Yahoo", client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient("Binance");
builder.Services.AddHttpClient("OKX");

// --- CORE APPLICATION SERVICES ---
builder.Services.AddThatsMyQuantApplication();
builder.Services.AddThatsMyQuantInfrastructure();
builder.Services.AddSingleton(new ServiceRuntimeState("Listener.Api"));
builder.Services.AddSingleton(new OperationalTelemetry("Listener.Api"));
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

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseRateLimiter();
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
}

app.Services.GetRequiredService<ServiceRuntimeState>().MarkReady();

app.UseMiddleware<HmacValidationMiddleware>();
app.UseMiddleware<ApiKeyValidationMiddleware>();
app.UseCors("AllowReact");

app.MapObservabilityEndpoints(app.Services);

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
