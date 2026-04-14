using System.IO;
using System.Net.Http;
using System.Windows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using ThatsMyQuant.Application.Common;
using ThatsMyQuant.Application.Security;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Dashboard.Auth;
using ThatsMyQuant.Dashboard.Services;
using ThatsMyQuant.Dashboard.ViewModels;
using ThatsMyQuant.Dashboard.ViewModels.SubViewModels;
using ThatsMyQuant.Infrastructure;
using ThatsMyQuant.Infrastructure.Ai;
using ThatsMyQuant.Infrastructure.Persistence;
using ThatsMyQuant.Infrastructure.Services;

namespace ThatsMyQuant.Dashboard;

public partial class App : System.Windows.Application
{
    public static IServiceProvider? ServiceProvider { get; private set; }
    private static readonly string CrashLogPath = Path.Combine(AppContext.BaseDirectory, "dashboard-crash.log");

    private static IConfiguration BuildRuntimeConfiguration()
        => new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("exchange_settings.json", optional: true)
            .AddEnvironmentVariables()
            .ApplySharedExchangeConfiguration()
            .Build();

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            RegisterGlobalExceptionHandlers();
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var services = new ServiceCollection();
            ConfigureServices(services);
            ServiceProvider = services.BuildServiceProvider();
            await EnsureDatabaseReadyAsync();

            var loggedIn = await RunLoginFlowAsync();
            if (!loggedIn)
            {
                Shutdown();
                return;
            }

            var config = BuildRuntimeConfiguration();
            var auditLogger = ServiceProvider.GetRequiredService<IAuditLogger>();
            Func<Task<bool>> reLoginCallback = RunLoginFlowAsync;
            var idleTimeoutService = IdleTimeoutService.FromConfiguration(config, auditLogger, reLoginCallback);

            var mainViewModel = ServiceProvider.GetRequiredService<MainViewModel>();
            idleTimeoutService.Attach(mainViewModel);

            var mainWindow = new MainWindow(mainViewModel, idleTimeoutService);
            MainWindow = mainWindow;

            mainViewModel.LogoutRequested += async (_, _) =>
            {
                mainWindow.Hide();
                var reLoggedIn = await RunLoginFlowAsync();
                if (reLoggedIn)
                {
                    mainWindow.Show();
                }
                else
                {
                    Shutdown();
                }
            };

            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            WriteCrashLog("Startup failure", ex);
            MessageBox.Show(
                $"Dashboard startup failed.\n\n{ex.Message}\n\nDetails were written to:\n{CrashLogPath}",
                "ThatsMyQuant Dashboard",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private async Task<bool> RunLoginFlowAsync()
    {
        var result = false;
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var loginWindow = new LoginWindow();
            result = loginWindow.ShowDialog() == true;
        });
        return result;
    }

    private async Task EnsureDatabaseReadyAsync()
    {
        if (ServiceProvider is null)
        {
            return;
        }

        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetService<QuantDbContext>();
        if (db is null)
        {
            return;
        }

        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var startupLogger = loggerFactory.CreateLogger("Dashboard.Startup");
        await SqliteCompatibilityBootstrapper.EnsureCompatibleSchemaAsync(db, BuildRuntimeConfiguration(), startupLogger);

        var authService = scope.ServiceProvider.GetService<IUserAuthService>();
        if (authService is not null)
        {
            await authService.SeedAsync();
        }

        var portfolioCache = scope.ServiceProvider.GetService<IPortfolioStateCache>();
        if (portfolioCache is not null)
        {
            await portfolioCache.InitializeAsync();
        }

        var strategyTuning = scope.ServiceProvider.GetService<StrategyTuningService>();
        if (strategyTuning is not null)
        {
            await strategyTuning.InitializeAsync();
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        var runtimeConfiguration = BuildRuntimeConfiguration();
        var geminiApiKey = runtimeConfiguration["TMQ_GEMINI_API_KEY"];
        var exchangeMode = SharedExchangeConfiguration.GetSelectedExchangeMode(runtimeConfiguration);
        var isOkxMode = exchangeMode.Equals("OKX", StringComparison.OrdinalIgnoreCase);

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ProcessSignalCommand).Assembly));

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
        services.AddHttpClient("OKX", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        services.AddSingleton<BorsaMarketDataService>();
        services.AddSingleton<YahooFinanceMarketDataService>();
        services.AddSingleton<IMarketDataService, ExchangeRoutedMarketDataService>();
        services.AddHttpClient<IAlertingService, TelegramAlertService>();
        services.AddHttpClient<IAlertingService, WebhookAlertService>();
        services.AddScoped<IReasoningProvider, DeepSeekReasoningService>();
        services.AddScoped<IAiAnalystService, AiAnalystService>();

        if (exchangeMode.Equals("Binance", StringComparison.OrdinalIgnoreCase) ||
            exchangeMode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase))
        {
            var binanceApiKey = runtimeConfiguration["TMQ_BINANCE_API_KEY"];
            var binanceApiSecret = runtimeConfiguration["TMQ_BINANCE_API_SECRET"];

            if (!string.IsNullOrEmpty(binanceApiKey) && !string.IsNullOrEmpty(binanceApiSecret))
            {
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
                services.AddSingleton<IExchangeService, MockBrokerService>();
            }
        }
        else if (exchangeMode.Equals("OKX", StringComparison.OrdinalIgnoreCase))
        {
            var okxApiKey = runtimeConfiguration["OKX:ApiKey"];
            var okxPassphrase = runtimeConfiguration["OKX:Passphrase"];
            var okxSecret = runtimeConfiguration["OKX:SecretKey"];

            if (!string.IsNullOrWhiteSpace(okxApiKey) &&
                !string.IsNullOrWhiteSpace(okxPassphrase) &&
                !string.IsNullOrWhiteSpace(okxSecret))
            {
                var vault = new ApiVault();
                vault.SecureInboundKey(okxSecret);
                services.AddSingleton(vault);
                services.AddSingleton<IExchangeService>(sp =>
                    new OkxExecutionService(
                        sp.GetRequiredService<ILogger<OkxExecutionService>>(),
                        sp.GetRequiredService<IHttpClientFactory>().CreateClient("OKX"),
                        runtimeConfiguration,
                        sp.GetService<ISharedPortfolioStateManager>(),
                        sp.GetRequiredService<ApiVault>()));
            }
            else
            {
                services.AddSingleton<IExchangeService, MockBrokerService>();
            }
        }
        else
        {
            services.AddSingleton<IExchangeService, MockBrokerService>();
        }

        services.AddScoped<ITechnicalAnalysisService, QuantIndicatorService>();
        services.AddTransient<BacktestEngineApp>();

        services.AddDbContext<QuantDbContext>(options =>
        {
            options.UseLazyLoadingProxies();
            options.UseConfiguredQuantDatabase(runtimeConfiguration);
        });
        services.AddScoped<ISignalRepository, Infrastructure.Repositories.SignalRepository>();
        services.AddSingleton<IPortfolioStateCache, PortfolioStateCache>();
        services.AddScoped<ITradeRepository, Infrastructure.Repositories.TradeRepository>();

        services.AddThatsMyQuantApplication();
        services.AddThatsMyQuantInfrastructure();

        services.AddSingleton<NewsFeedSim>();
        services.AddScoped<ISentimentService, GeminiSentimentService>();

        services.AddSingleton<ITradingLoopService, TradingLoopService>();
        services.AddSingleton<IStrategyHealthStore, JsonStrategyHealthStore>();
        services.AddSingleton<IStrategyPerformanceStore, JsonStrategyPerformanceStore>();
        services.AddSingleton<BinanceExchangeInfoProvider>();
        services.AddSingleton<IExchangeInfoProvider>(sp => sp.GetRequiredService<BinanceExchangeInfoProvider>());
        services.AddSingleton<IWebVitalsService, WebVitalsService>();
        services.AddSingleton<IUserPromptService, MessageBoxUserPromptService>();
        services.AddSingleton<IAuditLogger, ConsoleAuditLogger>();

        if (isOkxMode)
        {
            services.AddSingleton<IStreamingMarketDataService, OkxWebSocketService>();
        }
        else
        {
            services.AddSingleton<IStreamingMarketDataService, BinanceWebSocketService>();
        }
        services.AddSingleton<TickPipeline>();
        services.AddSingleton(sp => new MicrosecondPredictionEngine(
            "models/price_prediction.onnx",
            sp.GetRequiredService<ILogger<MicrosecondPredictionEngine>>()));

        services.AddSingleton<FocusedBacktestViewModel>();
        services.AddScoped<IFocusedAgent>(sp => sp.GetRequiredService<SentimentAgent>());

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

        services.AddTransient<MainViewModel>();
        services.AddScoped<IUserAuthService, UserAuthService>();
        services.AddLogging();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("DispatcherUnhandledException", args.Exception);
            MessageBox.Show(
                $"Dashboard hit an unexpected error.\n\n{args.Exception.Message}\n\nDetails were written to:\n{CrashLogPath}",
                "ThatsMyQuant Dashboard",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                WriteCrashLog("AppDomainUnhandledException", ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    private static void WriteCrashLog(string source, Exception ex)
    {
        try
        {
            var lines =
                $"[{DateTime.UtcNow:O}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}";
            File.AppendAllText(CrashLogPath, lines);
        }
        catch
        {
        }
    }
}
