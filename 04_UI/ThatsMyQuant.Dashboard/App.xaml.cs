using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ThatsMyQuant.Dashboard.ViewModels;
using ThatsMyQuant.Dashboard.ViewModels.SubViewModels;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Services;
using ThatsMyQuant.Infrastructure.Ai;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using MediatR;
using ThatsMyQuant.Contracts.Commands;

using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Application.Security;
using ThatsMyQuant.Dashboard.Services;
using ThatsMyQuant.Application.Common;
using ThatsMyQuant.Infrastructure;

namespace ThatsMyQuant.Dashboard;

public partial class App : System.Windows.Application
{
    public static IServiceProvider? ServiceProvider { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();
        await EnsureDatabaseReadyAsync();

        var loginWindow = new LoginWindow();
        if (loginWindow.ShowDialog() == true)
        {
            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
        }
        else
        {
            Shutdown();
        }

        base.OnStartup(e);
    }

    private async Task EnsureDatabaseReadyAsync()
    {
        using var scope = ServiceProvider?.CreateScope();
        var db = scope?.ServiceProvider.GetService<ThatsMyQuant.Infrastructure.Persistence.QuantDbContext>();
        if (db != null)
        {
            await db.Database.EnsureCreatedAsync();
            
            var portfolioCache = scope?.ServiceProvider.GetService<IPortfolioStateCache>();
            if (portfolioCache != null) await portfolioCache.InitializeAsync();
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        var geminiApiKey = Environment.GetEnvironmentVariable("TMQ_GEMINI_API_KEY");

        // Core Microservice Logic
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ProcessSignalCommand).Assembly));

        // HttpClient for Yahoo Finance
        services.AddHttpClient("Yahoo", client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddHttpClient("Binance", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        // BorsaMarketDataService is the automatic fallback when Yahoo is unavailable
        services.AddSingleton<BorsaMarketDataService>();
        services.AddSingleton<IMarketDataService, YahooFinanceMarketDataService>();
        services.AddHttpClient<IAlertingService, TelegramAlertService>();
        services.AddHttpClient<IAlertingService, WebhookAlertService>();
        services.AddScoped<IReasoningProvider, DeepSeekReasoningService>();
        services.AddScoped<IAiAnalystService, AiAnalystService>();
        // Exchange Service Integration
        var exchangeMode = Environment.GetEnvironmentVariable("TMQ_EXCHANGE_MODE") ?? "Mock";
        
        if (exchangeMode.Equals("Binance", StringComparison.OrdinalIgnoreCase) || 
            exchangeMode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase))
        {
            var binanceApiKey = Environment.GetEnvironmentVariable("TMQ_BINANCE_API_KEY");
            var binanceApiSecret = Environment.GetEnvironmentVariable("TMQ_BINANCE_API_SECRET");

            if (!string.IsNullOrEmpty(binanceApiKey) && !string.IsNullOrEmpty(binanceApiSecret))
            {
                // Institutional Grade: Vault keys in HSM immediately
                var vault = new ApiVault();
                vault.SecureInboundKey(binanceApiSecret);
                
                services.AddSingleton(vault);
                services.AddSingleton<IHardwareSecurityModule, HardwareSecurityModule>();
                
                var baseUrl = exchangeMode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase)
                    ? "https://testnet.binance.vision"
                    : "https://api.binance.com";

                services.AddHttpClient<IExchangeService, BinanceExchangeService>(client =>
                {
                    client.BaseAddress = new Uri(baseUrl);
                    client.Timeout = TimeSpan.FromSeconds(15);
                }).AddTypedClient<IExchangeService>((client, sp) => 
                    new BinanceExchangeService(
                        client, 
                        sp.GetRequiredService<IHardwareSecurityModule>(), 
                        sp.GetRequiredService<ILogger<BinanceExchangeService>>(), 
                        binanceApiKey));
            }
            else
            {
                // Fallback to Mock if keys are missing but mode is set
                services.AddSingleton<IExchangeService, MockBrokerService>();
            }
        }
        else
        {
            services.AddSingleton<IExchangeService, MockBrokerService>();
        }
        services.AddScoped<ITechnicalAnalysisService, QuantIndicatorService>();
        services.AddTransient<ThatsMyQuant.Application.Strategy.BacktestEngineApp>();
        
        // Database & Repositories
        services.AddDbContext<ThatsMyQuant.Infrastructure.Persistence.QuantDbContext>(options =>
        {
            options.UseLazyLoadingProxies();
            options.UseSqlite("Data Source=C:\\Users\\parlak\\Downloads\\ThatsMyQuant\\quant.db;Cache=Shared");
        });
        services.AddScoped<ISignalRepository, ThatsMyQuant.Infrastructure.Repositories.SignalRepository>();
        services.AddSingleton<IPortfolioStateCache, PortfolioStateCache>();
        services.AddScoped<ITradeRepository, ThatsMyQuant.Infrastructure.Repositories.TradeRepository>();

        // Swarm Intelligence & 5-Tier Agent Hierarchy
        services.AddThatsMyQuantApplication();
        services.AddThatsMyQuantInfrastructure();
        
        services.AddSingleton<NewsFeedSim>();
        services.AddScoped<ISentimentService, GeminiSentimentService>();
        
        // Dashboard Infrastructure
        services.AddSingleton<ITradingLoopService, TradingLoopService>();
        services.AddSingleton<IStrategyHealthStore, JsonStrategyHealthStore>();
        services.AddSingleton<BinanceExchangeInfoProvider>();
        services.AddSingleton<IExchangeInfoProvider>(sp => sp.GetRequiredService<BinanceExchangeInfoProvider>());
        services.AddSingleton<IWebVitalsService, WebVitalsService>();

        // Phase 8: HFT & Quantitative Foundation
        services.AddSingleton<IStreamingMarketDataService, BinanceWebSocketService>();
        services.AddSingleton<ThatsMyQuant.Application.Strategy.TickPipeline>();
        services.AddSingleton(sp => new ThatsMyQuant.Application.Strategy.MicrosecondPredictionEngine(
            "models/price_prediction.onnx", // Placeholder path
            sp.GetRequiredService<ILogger<ThatsMyQuant.Application.Strategy.MicrosecondPredictionEngine>>()));

        // FOCUSED BACKTEST (UI Flow)
        services.AddSingleton<FocusedBacktestViewModel>();
        services.AddScoped<IFocusedAgent>(sp => sp.GetRequiredService<SentimentAgent>());

        // AI (The Brain)
        if (!string.IsNullOrWhiteSpace(geminiApiKey))
        {
            services.AddKernel()
                    .AddGoogleAIGeminiChatCompletion("gemini-pro", geminiApiKey);
        }
        else
        {
            services.AddKernel();
            services.AddSingleton<IChatCompletionService, MockChatCompletionService>();
        }

        // UI
        services.AddTransient<MainWindow>();
        services.AddTransient<MainViewModel>();
        
        services.AddLogging();
    }
}
