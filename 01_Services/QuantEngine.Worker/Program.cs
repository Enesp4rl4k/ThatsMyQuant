using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
using ThatsMyQuant.Application.Observability;

PerformanceBootstrapper.OptimizeForHft();

var builder = Host.CreateApplicationBuilder(args);
// Defence-in-depth: explicitly load appsettings.Paper.json when running in paper mode so we
// never silently fall back to main appsettings.json defaults. The implicit env-overlay
// (`appsettings.{EnvironmentName}.json`) is fragile because Properties/launchSettings.json
// can override DOTNET_ENVIRONMENT — Launch-PaperTrade-Clean.ps1 passes --no-launch-profile
// to prevent this, but the explicit AddJsonFile here makes the path bulletproof either way.
{
    var tmqModeStartup = (Environment.GetEnvironmentVariable("TMQ_MODE") ?? string.Empty).ToLowerInvariant();
    if (tmqModeStartup == "paper")
    {
        // 2026-06-12: ContentRootPath-only probe was a silent kill switch.
        // Restart-Worker.ps1 -> Launch-Worker-Background.ps1 execs the built exe with
        // CWD = repo root, where appsettings.Paper.json does NOT exist -> File.Exists
        // guard silently skipped the overlay -> worker booted with ZERO EdgeLab
        // strategies and idled ("no symbols discovered" / "EdgeLab:InputDir not
        // configured"). Probe ContentRootPath first (dotnet run path), then the exe
        // directory (build output carries a copy). Paper mode with no Paper overlay
        // anywhere = config error -> fail fast (EX_CONFIG, same policy as the
        // credentials guard below).
        var paperJsonCandidates = new[]
        {
            Path.Combine(builder.Environment.ContentRootPath, "appsettings.Paper.json"),
            Path.Combine(AppContext.BaseDirectory, "appsettings.Paper.json"),
        };
        var foundPaperJson = paperJsonCandidates.FirstOrDefault(File.Exists);
        if (foundPaperJson is null)
        {
            Console.Error.WriteLine(
                "[FATAL] REFUSING TO START: TMQ_MODE=paper but appsettings.Paper.json was not found at " +
                string.Join(" nor ", paperJsonCandidates) +
                " — without it the worker runs with zero strategies. Launch via Launch-PaperTrade-Clean.ps1 " +
                "or fix the working directory.");
            Environment.Exit(78); // EX_CONFIG
        }
        builder.Configuration.AddJsonFile(foundPaperJson, optional: false, reloadOnChange: true);
    }
}
builder.Configuration.AddJsonFile("exchange_settings.json", optional: true, reloadOnChange: true);
builder.Configuration.ApplySharedExchangeConfiguration();
builder.Configuration.ApplySharedTelegramConfiguration();
var selectedExchangeMode = SharedExchangeConfiguration.GetSelectedExchangeMode(builder.Configuration);
var isBinanceMode =
    selectedExchangeMode.Equals("Binance", StringComparison.OrdinalIgnoreCase) ||
    selectedExchangeMode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase) ||
    selectedExchangeMode.Equals("BinanceFutures", StringComparison.OrdinalIgnoreCase) ||
    selectedExchangeMode.Equals("BinanceFuturesTestnet", StringComparison.OrdinalIgnoreCase);
var isOkxMode =
    selectedExchangeMode.Equals("OKX", StringComparison.OrdinalIgnoreCase) ||
    selectedExchangeMode.Equals("OKXSwap", StringComparison.OrdinalIgnoreCase);
var isMockMode = selectedExchangeMode.Equals("Mock", StringComparison.OrdinalIgnoreCase);
var binanceBaseUrl = selectedExchangeMode switch
{
    var mode when mode.Equals("BinanceTestnet", StringComparison.OrdinalIgnoreCase) => "https://testnet.binance.vision",
    var mode when mode.Equals("BinanceFutures", StringComparison.OrdinalIgnoreCase) => "https://fapi.binance.com",
    var mode when mode.Equals("BinanceFuturesTestnet", StringComparison.OrdinalIgnoreCase) => "https://testnet.binancefuture.com",
    _ => "https://api.binance.com"
};

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("Yahoo", c => c.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0"));
builder.Services.AddHttpClient("Binance", c =>
{
    c.BaseAddress = new Uri(binanceBaseUrl);
});
builder.Services.AddHttpClient("OKX");
builder.Services.AddHttpClient("OkxPublic", c =>
{
    c.BaseAddress = new Uri("https://www.okx.com");
    c.Timeout = TimeSpan.FromSeconds(10);
});
var geminiApiKey = builder.Configuration["Ai:GeminiApiKey"];

var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThatsMyQuant", "logs");
Directory.CreateDirectory(logDir);
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "QuantEngine.Worker")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] ({CorrelationId}) {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        new Serilog.Formatting.Compact.CompactJsonFormatter(),
        Path.Combine(logDir, "quantengine-.clef"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 50L * 1024 * 1024,
        rollOnFileSizeLimit: true,
        shared: true)
    .CreateLogger();
builder.Logging.ClearProviders();
builder.Services.AddSerilog();

// --- CORE APPLICATION SERVICES ---
builder.Services.AddThatsMyQuantInfrastructure();
builder.Services.AddThatsMyQuantApplication();
builder.Services.AddSingleton(new ServiceRuntimeState("QuantEngine.Worker"));
builder.Services.AddSingleton(new OperationalTelemetry("QuantEngine.Worker"));

// --- INFRASTRUCTURE ADAPTERS ---
builder.Services.AddSingleton<BorsaMarketDataService>();
builder.Services.AddSingleton<YahooFinanceMarketDataService>();
builder.Services.AddSingleton<IMarketDataService, ExchangeRoutedMarketDataService>();
builder.Services.AddScoped<IReasoningProvider, DeepSeekReasoningService>();
builder.Services.AddScoped<IAiAnalystService, AiAnalystService>();
builder.Services.AddScoped<ISentimentService, GeminiSentimentService>();
builder.Services.AddScoped<ITechnicalAnalysisService, QuantIndicatorService>();
// Exchange-aware streaming: OKX or Binance based on TMQ_EXCHANGE_MODE
if (isOkxMode)
{
    // Register concrete first so consumers (LiquidationCascadeHostedService) that depend
    // on the OKX-specific event surface can resolve it; expose the streaming abstraction
    // as a forward.
    builder.Services.AddSingleton<OkxWebSocketService>();
    builder.Services.AddSingleton<IStreamingMarketDataService>(sp => sp.GetRequiredService<OkxWebSocketService>());
}
else
{
    builder.Services.AddSingleton<IStreamingMarketDataService, BinanceWebSocketService>();
}
builder.Services.AddSingleton<BinanceExchangeInfoProvider>();
builder.Services.AddSingleton<IExchangeInfoProvider>(sp => sp.GetRequiredService<BinanceExchangeInfoProvider>());
builder.Services.AddSingleton<ITickLogger, JsonlTickLogger>();

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

// Bind TelegramOptions from "Telegram" configuration section so handlers that depend on
// IOptions<TelegramOptions> (e.g. TelegramPositionPushHandler) get the bot token + chat id.
// Without this binding the handler silently no-ops because BotToken/ChatId are empty —
// IRealtimeAlerter still works because it reads IConfiguration directly, masking the bug.
builder.Services.Configure<ThatsMyQuant.Contracts.Options.TelegramOptions>(
    builder.Configuration.GetSection("Telegram"));
builder.Services.AddHttpClient(); // for IHttpClientFactory used by TelegramPositionPushHandler

// MediatR
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(ProcessSignalCommand).Assembly);
    // Application assembly hosts TelegramPositionPushHandler + ChampionChallengerService etc.
    cfg.RegisterServicesFromAssembly(typeof(ThatsMyQuant.Application.Alerts.TelegramPositionPushHandler).Assembly);
    // Worker assembly hosts AiTradeReviewHandler (uses Microsoft.SemanticKernel which Application doesn't reference).
    cfg.RegisterServicesFromAssembly(typeof(ThatsMyQuant.Bot.AiTradeReviewHandler).Assembly);
});

// Persistence
builder.Services.AddDbContextPool<QuantDbContext>(options =>
    options.UseConfiguredQuantDatabase(builder.Configuration), poolSize: 128);
builder.Services.AddSingleton<SignalWriterChannel>();
builder.Services.AddHostedService<QuantPersistenceWorker>();
builder.Services.AddScoped<ISignalRepository, SignalRepository>();
builder.Services.AddSingleton<IPortfolioStateCache, PortfolioStateCache>();
// Signal Diagnostics — singleton in-memory registry shared across all hybrid executors
// (Funding/Pairs/EdgeLab) and the broadcast service. Read-only observability layer; never
// gates trade decisions. Worker-scope only — Listener.Api's own registration in its
// Program.cs is for the same DI shape but a separate process instance (when split).
builder.Services.AddSingleton<ThatsMyQuant.Application.Diagnostics.ISignalDiagnosticsRegistry,
    ThatsMyQuant.Infrastructure.Diagnostics.SignalDiagnosticsRegistry>();
builder.Services.AddScoped<ITradeRepository, TradeRepository>();
builder.Services.AddScoped<ReconciliationAgent>();
// Cross-process operator alert publisher — INSERTs into OperatorAlertEvents,
// Listener.Api's OperatorAlertDbReaderWorker republishes via SignalR.
builder.Services.AddSingleton<IOperatorAlertPublisher, DbOperatorAlertPublisher>();
// Phase 13 — heartbeat for the System Health page in the dashboard
builder.Services.AddHostedService<ServiceHeartbeatHostedService>();
// 2026-05-01 — operator-visible CMD heartbeat (single-line, every 60s, prefix [HB])
// Different from ServiceHeartbeatHostedService (silent DB write for dashboard);
// this one is for the human staring at the terminal: "is the bot ÇALIŞIYOR right now?"
builder.Services.AddHostedService<WorkerHeartbeatConsoleHostedService>();

var hasOkxCredentials =
    !string.IsNullOrWhiteSpace(builder.Configuration["OKX:ApiKey"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["OKX:Passphrase"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["OKX:SecretKey"]);
var hasBinanceCredentials =
    !string.IsNullOrWhiteSpace(builder.Configuration["TMQ_BINANCE_API_KEY"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["TMQ_BINANCE_API_SECRET"]);

var okxDemoFlag = builder.Configuration["OKX:IsDemoMode"] ?? builder.Configuration["OKX__IsDemoMode"];
var isOkxDemo = bool.TryParse(okxDemoFlag, out var okxDemo) && okxDemo;
var allowLiveTradingFlag = builder.Configuration["TMQ_ALLOW_LIVE_TRADING"];
var allowLiveTrading = bool.TryParse(allowLiveTradingFlag, out var allowLive) && allowLive;
var credentialSource = StartupSafetyPolicy.ResolveCredentialSource(hasOkxCredentials, hasBinanceCredentials);
var executionSyncEnabled = isOkxMode && hasOkxCredentials;

Log.Information(
    "QuantEngine.Worker startup: ExchangeMode={ExchangeMode} | OKX_Creds={HasOkxCreds} (Demo={OkxDemo}) | Binance_Creds={HasBinanceCreds} (BaseUrl={BinanceBaseUrl}) | LiveOverride={AllowLiveTrading} | CredentialSource={CredentialSource} | ExecutionSync={ExecutionSyncEnabled}",
    selectedExchangeMode,
    hasOkxCredentials,
    isOkxDemo,
    hasBinanceCredentials,
    binanceBaseUrl,
    allowLiveTrading,
    credentialSource,
    executionSyncEnabled);

// Paper mode uses PaperTradingService internally — the exchange is only consulted
// for market data and an optional balance probe. Missing credentials degrade those
// features but should not block the paper loop from starting. Live/real-trade modes
// keep the strict credential requirement.
var tmqMode = (builder.Configuration["TMQ_MODE"] ?? "live").Trim().ToLowerInvariant();
var requiresCredsForMode = !string.Equals(tmqMode, "paper", StringComparison.OrdinalIgnoreCase);

StartupSafetyPolicy.EnforceOrThrow(
    "QuantEngine.Worker",
    selectedExchangeMode,
    isMockMode,
    isOkxMode,
    isOkxDemo,
    allowLiveTrading,
    hasOkxCredentials,
    hasBinanceCredentials,
    requiresTradingCredentials: requiresCredsForMode);

// Execution routing follows the launcher-selected venue.
if (isOkxMode && hasOkxCredentials)
{
    builder.Services.AddSingleton<ThatsMyQuant.Application.Security.ApiVault>(sp =>
    {
        var vault = new ThatsMyQuant.Application.Security.ApiVault();
        vault.SecureInboundKey(builder.Configuration["OKX:SecretKey"]!);
        return vault;
    });
    builder.Services.AddSingleton<IExchangeService>(sp =>
        new OkxExecutionService(
            sp.GetRequiredService<ILogger<OkxExecutionService>>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("OKX"),
            builder.Configuration,
            sp.GetRequiredService<ISharedPortfolioStateManager>(),
            sp.GetService<ThatsMyQuant.Application.Security.ApiVault>(),
            sp.GetService<OperationalTelemetry>(),
            sp.GetRequiredService<IStreamingMarketDataService>()));
    builder.Services.AddSingleton<IExchangeExecutionSync>(sp =>
        new OkxExecutionSync(
            sp.GetRequiredService<ILogger<OkxExecutionSync>>(),
            builder.Configuration,
            sp.GetService<ThatsMyQuant.Application.Security.ApiVault>()));

    Log.Information("QuantEngine.Worker: OKX execution service registered (Demo={OkxDemo}).", isOkxDemo);
}
else if (isBinanceMode && hasBinanceCredentials)
{
    builder.Services.AddSingleton<IExchangeService>(sp =>
    {
        var keyVault = new ApiVault();
        var secretVault = new ApiVault();
        keyVault.SecureInboundKey(builder.Configuration["TMQ_BINANCE_API_KEY"]!);
        secretVault.SecureInboundKey(builder.Configuration["TMQ_BINANCE_API_SECRET"]!);

        return new BinanceExecutionService(
            sp.GetRequiredService<ILogger<BinanceExecutionService>>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("Binance"),
            keyVault,
            secretVault,
            sp.GetRequiredService<IStreamingMarketDataService>(),
            sp.GetRequiredService<ISharedPortfolioStateManager>(),
            sp.GetService<OperationalTelemetry>());
    });

    Log.Information("QuantEngine.Worker: Binance execution service registered in {Mode} mode.", selectedExchangeMode);
}
else
{
    // 2026-05-01 fail-fast guard: in paper/live mode, falling through to Mock+Chaos is ALWAYS
    // an operator/launcher bug — credentials weren't injected (e.g. raw `Start-Process exe`
    // bypassed Launch-PaperTrade-Clean.ps1, which is what decrypts the DPAPI blob and pushes
    // OKX__ApiKey/SecretKey/Passphrase into the env). Silent fallback to Mock means hours of
    // "why no fills" debugging while the bot pretends to scan but every order is fake.
    // Refuse to start so the mistake surfaces in the first second, not after a heartbeat tick.
    //
    // 2026-05-07 hardening: empty TMQ_MODE also triggers the guard. Previously the bypass
    // path (raw `dotnet run`) didn't set TMQ_MODE at all, so guard was skipped and worker
    // ran 24h in Mock+Chaos before being noticed. Default-secure: empty TMQ_MODE is treated
    // as paper (since this is the only sensible default for an operator-launched run).
    var tmqModeGuard = (Environment.GetEnvironmentVariable("TMQ_MODE") ?? string.Empty).ToLowerInvariant();
    var allowMockOverride = string.Equals(
        Environment.GetEnvironmentVariable("TMQ_ALLOW_MOCK_FALLBACK"),
        "true", StringComparison.OrdinalIgnoreCase);
    var isProtectedMode = tmqModeGuard == "paper" || tmqModeGuard == "live" || string.IsNullOrEmpty(tmqModeGuard);
    if (isProtectedMode && !isMockMode && !allowMockOverride)
    {
        var msg =
            $"REFUSING TO START: TMQ_MODE={tmqModeGuard} but no real-exchange credentials were resolved " +
            $"(ExchangeMode={selectedExchangeMode}, OKX_Creds={hasOkxCredentials}, Binance_Creds={hasBinanceCredentials}). " +
            "Falling back to Mock+Chaos in paper mode silently corrupts edge validation. " +
            "Fix: launch via Launch-PaperTrade-Clean.ps1 (decrypts DPAPI blob, exports OKX__ApiKey/SecretKey/Passphrase). " +
            "If you genuinely want Mock for a unit-test-style smoke run, set TMQ_EXCHANGE_MODE=Mock or TMQ_ALLOW_MOCK_FALLBACK=true.";
        Log.Fatal(msg);
        Console.Error.WriteLine("[FATAL] " + msg);
        Environment.Exit(78); // EX_CONFIG (sysexits.h) — config error, not transient
    }

    builder.Services.AddSingleton<IExchangeService>(sp =>
    {
        var inner = new MockBrokerService(sp.GetRequiredService<ILogger<MockBrokerService>>());
        return new ChaosExchangeDecorator(inner, sp.GetRequiredService<ILogger<ChaosExchangeDecorator>>());
    });

    Log.Warning("QuantEngine.Worker: Mock exchange execution registered (Chaos decorator enabled).");
}

builder.Services.AddSingleton<IVenueTradeFeeProvider>(sp =>
{
    var ex = sp.GetRequiredService<IExchangeService>();
    return ex as IVenueTradeFeeProvider ?? new NullVenueTradeFeeProvider();
});
// Expose IExchangePositionReader from the same OKX service when available — the hybrid
// reconciliation loop needs it to compare local state against actual OKX open positions.
builder.Services.AddSingleton<IExchangePositionReader>(sp =>
{
    var ex = sp.GetRequiredService<IExchangeService>();
    if (ex is IExchangePositionReader reader) return reader;
    Log.Warning("Active IExchangeService does not implement IExchangePositionReader — reconciliation will skip checks.");
    return new NullExchangePositionReader();
});
builder.Services.Replace(ServiceDescriptor.Singleton<IExecutionCostSnapshotCache, ExecutionCostSnapshotCache>());
// CRITICAL: Application's TryAddSingleton wires NullFundingRateProvider (returns 0 for every
// symbol/timestamp). The funding-alpha strategy uses EntryThreshold=0.0003 (3bp/8h) — a 0
// rate from the Null provider means ZERO entries ever fire. Replace with the real OKX-backed
// provider so paper-trade funding alpha actually sees real funding rates.
builder.Services.Replace(ServiceDescriptor.Singleton<IFundingRateProvider, ThatsMyQuant.Infrastructure.Services.OkxFundingRateProvider>());

builder.Services.TryAddSingleton<ThatsMyQuant.Application.Security.ApiVault>();
builder.Services.AddSingleton<IHardwareSecurityModule, HardwareSecurityModule>();

builder.Services.AddTransient<ISignalValidator, MarketHoursValidator>();
builder.Services.AddTransient<ISignalValidator, MinVolumeValidator>();
builder.Services.AddTransient<ISignalValidator, PriceSanityValidator>();

builder.Services.AddHostedService<WorkerManagementApiHostedService>();
builder.Services.AddHostedService<ExchangeExecutionSyncHostedService>();

// TMQ_MODE dispatch: paper | live (default). `backtest` runs via the CLI tool instead.
var runMode = (builder.Configuration["TMQ_MODE"] ?? "live").ToLowerInvariant();
switch (runMode)
{
    case "paper":
        builder.Services.AddSingleton<IRealtimeAlerter, TelegramRealtimeAlerter>();
        // GlobalCircuitBreaker — cross-tier daily PnL aggregation. All 4 tier executors
        // (Funding Long+Short, Pairs Long+Short) report close events here. When global
        // threshold breached, ALL tiers block new entries.
        builder.Services.AddSingleton<GlobalCircuitBreaker>();
        // NotionalExposureGuard — cross-tier total open notional cap. Each executor calls
        // TryReserve before opening; aborts if would breach cap. Released on close.
        // Prevents cash overrun when many tiers fire simultaneously.
        builder.Services.AddSingleton<NotionalExposureGuard>();
        // DrawdownLadderGate — tiered kill-switch from 2026-05-01 risk playbook. Operator-
        // psychology guard. -3% basis → 24h freeze, -6% → 72h, -10% → manual reset.
        // Existing positions still managed; only NEW entries blocked.
        builder.Services.AddSingleton<DrawdownLadderGate>();
        // DailyBackupHostedService — daily snapshot of SQLite + state files to a rotating
        // backup folder. Disk failure / corruption mid-trade is the single risk that can
        // zero account history overnight (Risk #5 from 2026-05-01 playbook). Postgres flip
        // is "ready" but un-deployed; until then this is the canonical safety net.
        builder.Services.AddHostedService<DailyBackupHostedService>();
        // CandidatePool — Tier B+ foundation. EdgeLab discovers candidates, this service
        // holds the registry of currently-running parallel paper validations. Singleton:
        // multiple components consult/update the pool (CLI commands, future GenericStrategyRunner,
        // future Telegram /candidates command). Persisted to disk so worker restart preserves
        // candidate registry. No live-trading impact until B+ runners are wired (deferred).
        builder.Services.AddSingleton<ThatsMyQuant.Application.Strategy.CandidatePool>();
        // StrategySampleStore — per-strategy ML sample collection (JSONL per StrategyTag).
        // Decoupled from the legacy 5-tensor ModelTrainingPipeline so funding+pairs samples
        // accumulate WITHOUT contaminating the directional model. FundingHybridExecutor +
        // PairsHybridExecutor write into this on close. First model train (post-Tier-A)
        // consumes from here. See ML_TRAINING_CALENDAR_2026_05_01.md for cadence.
        builder.Services.AddSingleton<ThatsMyQuant.Application.MachineLearning.IStrategySampleStore,
            ThatsMyQuant.Application.MachineLearning.StrategySampleStore>();
        builder.Services.AddSingleton<ThatsMyQuant.Application.Strategy.PaperPortfolioPersistenceService>();
        builder.Services.AddHostedService<ExchangeBalanceSyncHostedService>();
        builder.Services.AddHostedService<PaperTradingHostedService>();
        builder.Services.AddHostedService<PairsTradingPaperHostedService>();
        // Barbell pairs: Long tier = high-conviction (entry |z|≥2.5, multi-day hold, big size, low lev)
        //                Short tier = scalp (entry |z|≥1.5, hours hold, small size, high lev)
        // NOTE: use AddSingleton<IHostedService> directly because AddHostedService<T>(factory)
        // dedupes by concrete type via TryAddEnumerable — second registration becomes a no-op.
        builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => new PairsHybridExecutor(
            sp.GetRequiredService<ILogger<PairsHybridExecutor>>(),
            sp,
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<PaperPortfolio>(),
            sp.GetService<IRealtimeAlerter>(),
            tierName: "Long",
            globalBreaker: sp.GetService<GlobalCircuitBreaker>(),
            notionalGuard: sp.GetService<NotionalExposureGuard>(),
            drawdownLadder: sp.GetService<DrawdownLadderGate>(),
            persistence: sp.GetService<ThatsMyQuant.Application.Strategy.PaperPortfolioPersistenceService>(),
            mlSampleStore: sp.GetService<ThatsMyQuant.Application.MachineLearning.IStrategySampleStore>()));
        builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => new PairsHybridExecutor(
            sp.GetRequiredService<ILogger<PairsHybridExecutor>>(),
            sp,
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<PaperPortfolio>(),
            sp.GetService<IRealtimeAlerter>(),
            tierName: "Short",
            globalBreaker: sp.GetService<GlobalCircuitBreaker>(),
            notionalGuard: sp.GetService<NotionalExposureGuard>(),
            drawdownLadder: sp.GetService<DrawdownLadderGate>(),
            persistence: sp.GetService<ThatsMyQuant.Application.Strategy.PaperPortfolioPersistenceService>(),
            mlSampleStore: sp.GetService<ThatsMyQuant.Application.MachineLearning.IStrategySampleStore>()));
        // Funding execution path:
        //   - When Funding:HybridMode=off → legacy FundingAlphaPaperHostedService mirrors closed
        //     backtest trades into PaperPortfolio (local sim only, no OKX orders).
        //   - When Funding:HybridMode=dry-run|live → FundingHybridExecutor takes over: live signal
        //     evaluation per tick, real OKX demo order placement (live) or order-intent logging
        //     (dry-run). The legacy mirror service stays registered but its config check skips
        //     execution when HybridMode != off.
        builder.Services.AddHostedService<FundingAlphaPaperHostedService>();
        // EdgeLabSignalHostedService — consumes EdgeLab's signals/<strategy>/latest.json
        // bridge file (Quantlab repo) and rebalances the OKX paper book to match
        // target_weights. Contract: signals/README.md §"Bot Integration Contract".
        // Disabled until EdgeLab:InputDir is configured and EdgeLab:Enabled=true.
        builder.Services.Configure<ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabSignalOptions>(
            builder.Configuration.GetSection(
                ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabSignalOptions.SectionName));
        // Singleton — append-only JSONL store for dry-run intended orders. Daily digest
        // reads this to render the "EdgeLab Dry-Run (last 24h)" section. Path defaults
        // to %LocalAppData%\ThatsMyQuant\edgelab_dryrun_log.jsonl.
        builder.Services.AddSingleton<ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabDryRunLedger>();
        // Singleton — synthetic position book + JSONL hypothetical-PnL ledger.
        // Synthetic portfolio: per-cycle EdgeLab-only positions, drives delta math
        // (instead of zero baseline or contaminated OKX net) and persists across
        // worker restarts. Reconciliation ledger: per-cycle hypothetical PnL row;
        // promotion gate (future) reads it to evaluate live readiness.
        builder.Services.AddSingleton<ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabSyntheticPortfolio>();
        builder.Services.AddSingleton<ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabReconciliationLedger>();
        builder.Services.AddHostedService<EdgeLabSignalHostedService>();
        // Additional EdgeLab strategies (dry-run only, never auto-promoted). Quantlab adds new
        // signal streams under signals/<strategy>/; each one needs its own hosted-service instance
        // with its OWN per-strategy ledger files so synthetic position / dry-run log / reconciliation
        // paths don't fight each other (the singletons above are reserved for the canonical
        // realized_vol_4h_xs_reverse strategy). Read EdgeLab:AdditionalStrategies from config —
        // missing/empty array is a no-op so removing strategies is also safe.
        // Promotion gate (per memory funding_universe_rebuild_2026_05_03): N≥30 trades + Sharpe≥0.5
        // + DSR p>0.7 before flipping any to live.
        // Periodic paper-shadow Sharpe / cumulative-PnL emitter for ALL EdgeLab strategies.
        // Reads each strategy's reconciliation ledger every 6h (configurable) and logs one
        // summary line per strategy — operator can grep `[paper-shadow]` to track real
        // Sharpe converging vs. backtest figures, especially for the 2026-05-03 strategies
        // whose reported Sharpes (5.27 / 7.68 / 3.13) need real-execution validation.
        builder.Services.AddHostedService<EdgeLabPaperShadowMonitor>();
        // EdgeLabRealizedPnLDumper — daily CSV dump of cycle-level realized PnL to
        // Quantlab/data/realized_pnl/<strategy>/<date>.csv. Critical for the $2K
        // capital cycle (master plan v2): realized vs backtest gap calibration.
        // Quantlab side reads these files via scripts/sanity_realized_vs_backtest.py.
        builder.Services.AddHostedService<EdgeLabRealizedPnLDumperHostedService>();
        // 2026-05-17 Quantlab handoff §1 — per-strategy PER-TRADE realized PnL CSV.
        // The Dumper above emits cycle-level synthetic PnL; live_monitor.py and
        // track_realized.py need per-trade rows grouped by StrategyTag to drive
        // realized-Sharpe kill-switch decisions and maker-fill-rate watch tier.
        // Reads from paper_portfolio.json (PaperPortfolio.ClosedPositions) which is
        // the actual fill ledger in paper mode; SQLite Trades is dormant for this
        // path (verified 2026-05-17: portfolio JSON had 126 closed vs DB had 0).
        builder.Services.AddHostedService<PerStrategyRealizedPnlCsvHostedService>();
        var additionalEdgeLabStrategies = builder.Configuration
            .GetSection("EdgeLab:AdditionalStrategies").Get<string[]>() ?? Array.Empty<string>();
        foreach (var strategyName in additionalEdgeLabStrategies)
        {
            var stratNameCaptured = strategyName;  // closure capture
            builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp =>
            {
                var baseOpts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<
                    ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabSignalOptions>>().Value;
                var perStratOpts = new ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabSignalOptions
                {
                    Enabled = baseOpts.Enabled,
                    InputDir = baseOpts.InputDir,
                    Strategy = stratNameCaptured,
                    Timeframe = baseOpts.Timeframe,
                    PollIntervalSeconds = baseOpts.PollIntervalSeconds,
                    MinDeltaUsd = baseOpts.MinDeltaUsd,
                    OrderType = baseOpts.OrderType,
                    HybridMode = "dry-run",  // forced — new candidates always start dry-run
                };
                var dataDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ThatsMyQuant");
                var dryRun = new ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabDryRunLedger(
                    System.IO.Path.Combine(dataDir, $"edgelab_dryrun_log_{stratNameCaptured}.jsonl"));
                var synth = new ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabSyntheticPortfolio(
                    System.IO.Path.Combine(dataDir, $"edgelab_synthetic_{stratNameCaptured}.json"));
                var recon = new ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabReconciliationLedger(
                    System.IO.Path.Combine(dataDir, $"edgelab_dryrun_pnl_{stratNameCaptured}.jsonl"));
                return new EdgeLabSignalHostedService(
                    sp.GetRequiredService<ILogger<EdgeLabSignalHostedService>>(),
                    sp,
                    Microsoft.Extensions.Options.Options.Create(perStratOpts),
                    sp.GetRequiredService<ThatsMyQuant.Contracts.Models.PaperPortfolio>(),
                    dryRun, synth, recon,
                    sp.GetService<ThatsMyQuant.Contracts.Interfaces.IOperatorAlertPublisher>());
            });
        }

        // EdgeLabSingleAsset — single-asset (directional) signal consumer added 2026-05-08
        // for the $2K capital cycle (master plan: Quantlab/hypotheses/MASTER_PLAN_2K_2026_05_08.md).
        // Reads same signals/<strategy>/latest.json contract but with metadata.strategy_kind ==
        // "single_asset"; one hosted-service instance per entry in EdgeLabSingleAsset:Strategies.
        // Defaults to disabled — operator opts in by setting Enabled=true and listing strategies.
        // Order placement is currently scaffolded as dry-run-log only; live order wiring is a
        // TODO inside EdgeLabSingleAssetHostedService.ApplyCycle (operator review required).
        var singleAssetSection = builder.Configuration.GetSection(
            ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabSingleAssetOptions.SectionName);
        var singleAssetOpts = singleAssetSection.Get<ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabSingleAssetOptions>()
                              ?? new ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabSingleAssetOptions();
        if (singleAssetOpts.Enabled && singleAssetOpts.Strategies != null)
        {
            // Per-strategy timeframe parsing — single-asset strategies may run on
            // different cadences (1h for ada/funding_zscore variants, daily for
            // unlock-event strategies). For now, use a fixed 1h fallback; operator
            // can extend to per-strategy timeframe map in future.
            var defaultTimeframe = TimeSpan.FromHours(1);
            foreach (var saName in singleAssetOpts.Strategies)
            {
                var saNameCaptured = saName;
                builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp =>
                {
                    var dataDir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ThatsMyQuant");
                    var saDryRun = new ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabDryRunLedger(
                        System.IO.Path.Combine(dataDir, $"edgelab_dryrun_log_{saNameCaptured}.jsonl"));
                    var saSynth = new ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabSyntheticPortfolio(
                        System.IO.Path.Combine(dataDir, $"edgelab_synthetic_{saNameCaptured}.json"));
                    var saRecon = new ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabReconciliationLedger(
                        System.IO.Path.Combine(dataDir, $"edgelab_dryrun_pnl_{saNameCaptured}.jsonl"));
                    // [Sprint 2 — 2026-05-10] Per-strategy override resolved at DI-registration
                    // time so each hosted-service instance carries its own weight + promotion gate
                    // independently. Falls back to equal-share defaults if the operator did not
                    // configure an override for this strategy.
                    var saOverride = singleAssetOpts.GetEffectiveOverride(saNameCaptured);
                    return new EdgeLabSingleAssetHostedService(
                        sp.GetRequiredService<ILogger<EdgeLabSingleAssetHostedService>>(),
                        sp,
                        singleAssetOpts,
                        saNameCaptured,
                        defaultTimeframe,
                        sp.GetRequiredService<ThatsMyQuant.Contracts.Models.PaperPortfolio>(),
                        saDryRun, saSynth, saRecon,
                        sp.GetService<ThatsMyQuant.Contracts.Interfaces.IOperatorAlertPublisher>(),
                        saOverride);
                });
            }
        }

        // Barbell tier registrations:
        //   - Long  : conviction bucket (high threshold, big size, low lev, low frequency)
        //   - Short : scalp bucket (low threshold, small size, high lev, high frequency)
        // Both share runner/portfolio/exchange but maintain separate state files + position caps.
        // Each tier reads Funding:Long:* / Funding:Short:* config (falls back to Funding:* legacy).
        // To run single-tier mode: set HybridMode=off on one tier and the other tier carries the strategy alone.
        builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => new FundingHybridExecutor(
            sp.GetRequiredService<ILogger<FundingHybridExecutor>>(),
            sp,
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<PaperPortfolio>(),
            sp.GetService<IRealtimeAlerter>(),
            sp.GetService<ThatsMyQuant.Application.Strategy.ProMode.FundingOpportunityScanner>(),
            tierName: "Long",
            globalBreaker: sp.GetService<GlobalCircuitBreaker>(),
            notionalGuard: sp.GetService<NotionalExposureGuard>(),
            drawdownLadder: sp.GetService<DrawdownLadderGate>(),
            persistence: sp.GetService<ThatsMyQuant.Application.Strategy.PaperPortfolioPersistenceService>(),
            mlSampleStore: sp.GetService<ThatsMyQuant.Application.MachineLearning.IStrategySampleStore>()));
        builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => new FundingHybridExecutor(
            sp.GetRequiredService<ILogger<FundingHybridExecutor>>(),
            sp,
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<PaperPortfolio>(),
            sp.GetService<IRealtimeAlerter>(),
            sp.GetService<ThatsMyQuant.Application.Strategy.ProMode.FundingOpportunityScanner>(),
            tierName: "Short",
            globalBreaker: sp.GetService<GlobalCircuitBreaker>(),
            notionalGuard: sp.GetService<NotionalExposureGuard>(),
            drawdownLadder: sp.GetService<DrawdownLadderGate>(),
            persistence: sp.GetService<ThatsMyQuant.Application.Strategy.PaperPortfolioPersistenceService>(),
            mlSampleStore: sp.GetService<ThatsMyQuant.Application.MachineLearning.IStrategySampleStore>()));
        builder.Services.AddHostedService<HybridReconciliationHostedService>();
        builder.Services.AddHostedService<DailyTelegramDigestHostedService>();
        // [2026-05-14 unattended hardening] Daily 24h ledger reconciliation: count + volume + fee
        // match between local DB closed trades and OKX fills-history. Fires Telegram if drift > tol.
        builder.Services.AddHostedService<DailyTradeReconciliationHostedService>();
        // [2026-05-14 unattended hardening] Hard kill-switch: force-flatten on -8% drawdown
        // OR -3% daily PnL. Singleton so executors can read IsKilled before opening entries.
        builder.Services.AddSingleton<KillSwitchHostedService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<KillSwitchHostedService>());
        // [2026-05-14 unattended hardening] Pre-entry risk gate: per-entry safety checks
        // (fat-finger 15%, position-count 10, funding-drift 100bp). Singleton consulted by
        // executors via constructor injection. Optional — checks return Allow() when disabled.
        builder.Services.AddSingleton<PreEntryRiskGate>();
        // [2026-05-14 unattended hardening] Capture full Risk:/OrderExecution:/Paper:/etc.
        // config to a timestamped JSON snapshot at every boot. Index file tracks which
        // snapshots changed config vs prior boot. Helps answer "what config was running
        // on day X?" 15 days later.
        builder.Services.AddHostedService<ConfigSnapshotHostedService>();
        // [2026-05-14 unattended hardening — Layer 4 from cancel-verify FSM plan]
        // At boot (after 60s grace), reconcile OKX open orders with local active trades.
        // Orphan venue orders trigger Telegram alert. Periodic sync handles ongoing drift.
        builder.Services.AddHostedService<BootOrphanSweepHostedService>();
        // [2026-05-14 bug-hunt round 5 — Bug #6] SQLite WAL grows unbounded under
        // continuous-INSERT load (telemetry, alerts, trade events). Periodic
        // wal_checkpoint(TRUNCATE) every 6h, optimize every 24h, disk-space alarm < 2GB.
        builder.Services.AddHostedService<SqliteMaintenanceHostedService>();
        // [2026-05-17 CRITICAL fix — clock drift] Windows W32Time may be disabled/mis-
        // configured (Source: Local CMOS Clock). When local clock drifts >30s from OKX
        // server, every OKX REST call fails with 50102 "Timestamp request expired".
        // This service fetches OKX server time at boot + every 5min, applies the offset
        // to OkxExecutionService.GetIsoTimestamp() so requests stay accepted even when
        // Windows clock is drifting. Critical Telegram alert on > 25s drift.
        builder.Services.AddHostedService<OkxClockSync>();
        // [2026-05-15 ledger drift fix — Option C] EdgeLab strategies fill via
        // OkxExecutionService → DB.Trades but never touch PaperPortfolio. All monitors
        // (heartbeat, TCA, ShadowAudit, KillSwitch, digest) read PaperPortfolio →
        // operator-blind to live OKX state. This service mirrors DB Trades into
        // PaperPortfolio.OpenPositions/ClosedPositions every 60s with ExecutionMode
        // marker "LIVE_DB". Paper-only entries preserved.
        builder.Services.AddHostedService<DbToPortfolioSyncHostedService>();
        // [2026-06-12 ledger drift — the "proper fix" DbToPortfolioSync flagged as
        // Sprint-Post-Vacation #1] EdgeLab/hybrid/OKX closes land in PaperPortfolio.ClosedPositions
        // but nothing wrote db.Trades, leaving the kill switch + per-strategy digest blind
        // (they read db.Trades). This service mirrors closed positions → db.Trades every 5min,
        // forward-only from a persisted cutoff (keeps pre-registered-test sample clean),
        // idempotent by content hash. Consumers unchanged.
        builder.Services.AddHostedService<ClosedPositionToTradeMirrorHostedService>();
        // [2026-05-16 per-strategy attribution] OKX positions-history doesn't carry strategy
        // attribution. This ledger captures (time, symbol, strategy) on every EdgeLab live fill;
        // DbToPortfolioSync looks up by time+symbol when mirroring closed positions so the
        // digest shows per-strategy PnL (not "OkxLive" collapse). Needed by Quantlab
        // track_realized.py for OOS validation.
        builder.Services.AddSingleton<EdgeLabFillAttributionLedger>();
        // TelegramCommandHostedService — two-way bot: long-polls /status /pnl /positions /help.
        // Independent of digest service; replies only to the configured chat id.
        builder.Services.AddHostedService<TelegramCommandHostedService>();
        // ManualCloseRequestProcessor — polls cross-process ManualCloseRequests table every 3s
        // for operator-initiated close requests from the Cockpit UI.
        builder.Services.AddHostedService<ManualCloseRequestProcessor>();
        // PairsDiscoveryHostedService — every 6h scans 30-symbol crypto universe for fresh
        // cointegrating pair candidates, writes to LocalAppData/ThatsMyQuant/pairs_discovery_candidates.json
        // for human review. NEVER auto-promotes; operator manually edits AllowedPairsCsv.
        builder.Services.AddHostedService<PairsDiscoveryHostedService>();
        // [P1.1] LiquidationCascadeHostedService — consumes OKX liquidation-orders WS channel,
        // detects cascades (default 5M BTC / 1M alt notional in 5min @ 70% dominance) and
        // dispatches a fade ExecuteTradeCommand. Disabled by default
        // (LiquidationCascade:Enabled=true to arm). Requires OKX mode (concrete WS service).
        if (isOkxMode)
        {
            builder.Services.AddOptions<ThatsMyQuant.Contracts.Options.LiquidationCascadeOptions>()
                .BindConfiguration(ThatsMyQuant.Contracts.Options.LiquidationCascadeOptions.SectionName);
            builder.Services.AddHostedService<LiquidationCascadeHostedService>();
        }
        // FundingScoutHostedService — every 5m polls watchlist funding rates, fires Telegram
        // alert when |rate| crosses 0.5bp (half of hybrid entry threshold). Heads-up so the
        // operator sees funding building before a real entry signal triggers.
        builder.Services.AddHostedService<FundingScoutHostedService>();
        // MacroEventCalendarService — polls economic calendar (FOMC, CPI, NFP) and exposes a
        // global pause flag (MacroEventCalendarService.Current). Hybrid executors check this
        // and block NEW entries within the [pre-event, +post-event] window. Existing
        // positions still managed. Auto-resumes after the post-window.
        builder.Services.AddHostedService<MacroEventCalendarService>();
        // Real-time TCA — every 30min runs cost-leakage analysis on rolling closed-trade window.
        // Read-only; alerts when leakage > threshold (default 30%).
        builder.Services.AddHostedService<TcaLiveAnalyzerHostedService>();
        // 2026-05-07 (Sprint 1.1): Funding-edge decay early-warning. Per-symbol rolling-N
        // (funding_received / fees_paid) ratio. Alerts when ratio < 1.5 (carry edge eroding
        // before Sharpe shows it). Premortem #1 detector. Read-only; downstream PromotionGate
        // (Sprint 2 SPRT) consumes the state file as a hard veto on live promotion.
        builder.Services.AddHostedService<FundingFeeRatioMonitorHostedService>();
        // 2026-05-07 (Sprint 1.2): Demo→live execution drift early-warning. Pulls last 30d
        // fills from OKX /trade/fills-history and computes maker/(maker+taker) using the
        // execType field. Alerts when ratio < 0.75 (queue race lost, fees ~2x assumed).
        // Premortem #4 detector.
        builder.Services.AddHostedService<MakerFillRatioMonitorHostedService>();
        // 2026-05-11: Maker-fill-ratio root-cause fix. OkxWebSocketService was registered
        // but never connected — books5 subscription empty → OkxExecutionService had no
        // top-of-book snapshot → post-only fell back to mid → 0% maker fill across 336
        // fills. This service reads EdgeLab strategies' latest.json target_weights and
        // calls ConnectAsync so books5 + trades stream into _latestOrderBooks, restoring
        // the post-only nudge path.
        if (isOkxMode)
        {
            builder.Services.AddHostedService<OrderBookSubscriptionHostedService>();
        }
        // 2026-05-07 (Sprint 1.3): Diversification-myth detector. Pairwise Pearson correlation
        // across daily-bucketed PnL of every active StrategyTag (rolling 60d). Off-diagonal
        // > 0.4 = factor concentration disguised as diversification. Premortem #5 detector.
        // Sprint 3.1 HRP allocator consumes the matrix as input weights.
        builder.Services.AddHostedService<PairwiseCorrelationMonitorHostedService>();
        // 2026-05-07 (Sprint 3.2): Convert PortfolioRiskLive from observational to enforcing.
        // Singleton flag tripped when VaR/CVaR breach observed; executor pre-entry path
        // refuses new positions until cleared by next snapshot.
        builder.Services.AddSingleton<ThatsMyQuant.Application.Risk.PortfolioRiskBlockState>();
        // 2026-05-07 (Sprint 3.1 Phase 1): HRP allocator compute & log. Reads daily PnL
        // covariance, runs Lopez de Prado 2016 HRP, persists weights to state file.
        // Phase 2 (next sprint) wires weights into executor sizing.
        builder.Services.AddHostedService<HrpAllocatorMonitorHostedService>();
        // Shadow execution audit — every 60min, re-prices closed trades through square-root impact
        // model + spread + commission and compares to paper PnL. Tells us if alpha is decaying
        // because EDGE is failing or EXECUTION is failing.
        builder.Services.AddHostedService<ShadowAuditLiveHostedService>();
        // Portfolio risk analytics — every 15min publishes Kelly recommendation + VaR/CVaR.
        // Alerts when tail risk exceeds % of basis. Read-only.
        builder.Services.AddHostedService<PortfolioRiskLiveHostedService>();
        // Daily 23:55 UTC paper→live promotion gate verdict (objective criteria, not gut feel).
        builder.Services.AddHostedService<PromotionGateLiveHostedService>();
        // [Sprint 3 — 2026-05-10] Daily 23:30 UTC per-strategy realized-PnL digest.
        // With 11 EdgeLab strategies + Sprint 2 attribution fix, the operator needs daily
        // visibility on which edges are earning vs decaying.
        builder.Services.AddHostedService<PerStrategyDigestHostedService>();
        // [Sprint 4 — 2026-05-10] Markout snapshot service — every fill gets +1s/+10s/+60s/+300s
        // mark-price snapshots for adverse-selection TCA. Singleton ledger so restart-safe append.
        builder.Services.AddSingleton<ThatsMyQuant.Application.Strategy.EdgeLab.MarkoutLedger>(
            _ => new ThatsMyQuant.Application.Strategy.EdgeLab.MarkoutLedger());
        builder.Services.AddHostedService<MarkoutSnapshotHostedService>();
        // [Sprint 5 — 2026-05-10] B1 partial: boot-time basis sanity check. ExchangeBalanceSync
        // overwrites PaperPortfolio.Cash + InitialBalance with the real OKX demo balance after
        // ~5s. The historical "$96K vs $5K" mismatch was a stale persistence snapshot from before
        // the $2K-cycle pivot. We log the trio (config, OKX-demo-after-sync, runtime) once on
        // first heartbeat so operator + future post-mortems can verify they agreed at boot.
        builder.Services.AddHostedService<BasisSanityCheckHostedService>();

        // [Sprint 6 — 2026-05-10] Quantlab integration contract: per-strategy realized-Sharpe
        // kill switch. KillSwitchState is a singleton JSON-backed mutex; the hosted service
        // re-evaluates hourly and on boot. EdgeLabSingleAssetHostedService reads multiplier
        // every cycle and applies it to its sizing target.
        builder.Services.AddSingleton<ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabKillSwitchState>(
            _ => new ThatsMyQuant.Application.Strategy.EdgeLab.EdgeLabKillSwitchState());
        // Register concrete + as IHostedService so EdgeLabSingleAssetHostedService can inject the
        // concrete to push threshold updates without polluting the IHostedService interface.
        builder.Services.AddSingleton<EdgeLabKillSwitchHostedService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<EdgeLabKillSwitchHostedService>());
        Log.Information(
            "QuantEngine.Worker: TMQ_MODE=paper. Hosted services: ExchangeBalanceSync, PaperTrading (directional disabled), Pairs, FundingMirror+FundingHybrid, PairsDiscovery, TcaLiveAnalyzer, ShadowAuditLive, PortfolioRiskLive, PromotionGateLive, DailyTelegramDigest, PerStrategyDigest, MarkoutSnapshot.");
        break;

    case "live":
    default:
        builder.Services.AddHostedService<TradeBotWorker>();
        if (bool.TryParse(builder.Configuration["QuantEngine:LiveSignalScan:Enabled"], out var liveScan) && liveScan)
        {
            var symbols = builder.Configuration["QuantEngine:LiveSignalScan:Symbols"] ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(symbols))
            {
                builder.Services.AddHostedService<LiveSignalScanHostedService>();
                Log.Information("QuantEngine.Worker: LiveSignalScanHostedService registered (symbols: {Symbols}).", symbols);
            }
            else
            {
                Log.Warning("QuantEngine.Worker: LiveSignalScan:Enabled=true but QuantEngine:LiveSignalScan:Symbols is empty; scan service not registered.");
            }
        }

        Log.Information(
            "QuantEngine.Worker: TMQ_MODE={Mode}. TradeBotWorker registered. HFT/tick signals are produced in Execution.Worker (TickPipeline); optional candle scan runs here when configured.",
            runMode);
        break;
}

var host = builder.Build();
using (var scope = host.Services.CreateScope())
{
    var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
    var startupLogger = loggerFactory.CreateLogger("QuantEngine.Worker.Startup");
    var db = scope.ServiceProvider.GetRequiredService<QuantDbContext>();
    await SqliteCompatibilityBootstrapper.EnsureCompatibleSchemaAsync(db, builder.Configuration, startupLogger);

    var exchangeInfo = scope.ServiceProvider.GetRequiredService<BinanceExchangeInfoProvider>();
    try
    {
        await exchangeInfo.InitializeAsync();
    }
    catch (Exception ex)
    {
        startupLogger.LogWarning(ex, "Binance exchange metadata initialization failed. QuantEngine.Worker will continue with degraded symbol metadata.");
    }

    var portfolioCache = scope.ServiceProvider.GetRequiredService<IPortfolioStateCache>();
    await portfolioCache.InitializeAsync();

    var promotion = scope.ServiceProvider.GetRequiredService<StrategyPromotionService>();
    await promotion.InitializeAsync();
}

host.Services.GetRequiredService<ServiceRuntimeState>().MarkReady();

await host.RunAsync();
