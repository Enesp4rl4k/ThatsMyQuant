using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Infrastructure.Services;
using ThatsMyQuant.Infrastructure.Persistence;
using ThatsMyQuant.Infrastructure.Repositories;
using ThatsMyQuant.Execution.Worker;
using ThatsMyQuant.Application.Commands;
using ThatsMyQuant.Application.Common;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Infrastructure.Ai;
using ThatsMyQuant.Infrastructure.Ai;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

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
    options.UseSqlite(builder.Configuration.GetConnectionString("QuantDb") ?? "Data Source=C:\\Users\\parlak\\Downloads\\ThatsMyQuant\\quant.db"));
builder.Services.AddScoped<ISignalRepository, SignalRepository>();
builder.Services.AddScoped<ITradeRepository, TradeRepository>();

// Execution
builder.Services.AddSingleton<IExchangeService, MockBrokerService>();
builder.Services.AddHostedService<ExecutionWorker>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<QuantDbContext>().Database.EnsureCreated();
}

host.Run();
