using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Serilog;
using Microsoft.SemanticKernel;
using ThatsMyQuant.Bot;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Infrastructure.Services;
using ThatsMyQuant.Infrastructure.Ai;
using ThatsMyQuant.Application.Commands;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Infrastructure.Repositories;
using ThatsMyQuant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using MediatR;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Application.Common;

PerformanceBootstrapper.OptimizeForHft();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHttpClient();
var geminiApiKey = builder.Configuration["Ai:GeminiApiKey"];

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
builder.Logging.ClearProviders();
builder.Services.AddSerilog();

// --- CORE APPLICATION SERVICES ---
builder.Services.AddThatsMyQuantApplication();

// --- INFRASTRUCTURE ADAPTERS ---
builder.Services.AddSingleton<BorsaMarketDataService>();
builder.Services.AddSingleton<IMarketDataService, YahooFinanceMarketDataService>();
builder.Services.AddSingleton<IAiReasoningService, DeepSeekReasoningService>();
builder.Services.AddSingleton<IAiAnalystService, AiAnalystService>();
builder.Services.AddSingleton<ISentimentService, GeminiSentimentService>();
builder.Services.AddSingleton<ITechnicalAnalysisService, QuantIndicatorService>();

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
builder.Services.AddDbContext<QuantDbContext>(options => 
    options.UseSqlite(builder.Configuration.GetConnectionString("QuantDb") ?? "Data Source=quant.db"));
builder.Services.AddScoped<ISignalRepository, SignalRepository>();
builder.Services.AddScoped<ITradeRepository, TradeRepository>();
builder.Services.AddScoped<ReconciliationAgent>();

// Execution (Decorated with Chaos in QuantEngine for testing)
builder.Services.AddSingleton<IExchangeService>(sp => 
{
    var inner = new MockBrokerService(sp.GetRequiredService<ILogger<MockBrokerService>>()); 
    return new ChaosExchangeDecorator(inner, sp.GetRequiredService<ILogger<ChaosExchangeDecorator>>());
});

builder.Services.AddHostedService<TradeBotWorker>();

var host = builder.Build();
using (var scope = host.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<QuantDbContext>().Database.EnsureCreated();
}

host.Run();
