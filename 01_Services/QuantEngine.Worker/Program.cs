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

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
builder.Logging.ClearProviders();
builder.Services.AddSerilog();

// --- Shared infrastructure ---
builder.Services.AddThatsMyQuantCommonInfrastructure(builder.Configuration);

// --- AI / Reasoning ---
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

// MediatR
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ProcessSignalCommand).Assembly));

// Persistence
var connectionString = builder.Configuration.GetConnectionString("QuantDb") ?? "Data Source=quant.db;Cache=Shared";
builder.Services.AddDbContextPool<QuantDbContext>(options =>
    options.UseSqlite(connectionString), poolSize: 128);
builder.Services.AddSingleton<SignalWriterChannel>();
builder.Services.AddHostedService<QuantPersistenceWorker>();
builder.Services.AddScoped<ISignalRepository, SignalRepository>();
builder.Services.AddSingleton<IPortfolioStateCache, PortfolioStateCache>();
builder.Services.AddScoped<ITradeRepository, TradeRepository>();
builder.Services.AddScoped<ReconciliationAgent>();

// Execution (Decorated with Chaos in QuantEngine for testing)
builder.Services.AddSingleton<IExchangeService>(sp => 
{
    var inner = new MockBrokerService(sp.GetRequiredService<ILogger<MockBrokerService>>()); 
    return new ChaosExchangeDecorator(inner, sp.GetRequiredService<ILogger<ChaosExchangeDecorator>>());
});

builder.Services.AddSingleton<ThatsMyQuant.Application.Security.ApiVault>();
builder.Services.AddSingleton<IHardwareSecurityModule, HardwareSecurityModule>();

builder.Services.AddTransient<ISignalValidator, MarketHoursValidator>();
builder.Services.AddTransient<ISignalValidator, MinVolumeValidator>();
builder.Services.AddTransient<ISignalValidator, PriceSanityValidator>();

builder.Services.AddHostedService<TradeBotWorker>();

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
