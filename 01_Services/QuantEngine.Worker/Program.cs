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
        var explicitPaperJson = Path.Combine(builder.Environment.ContentRootPath, "appsettings.Paper.json");
        if (File.Exists(explicitPaperJson))
        {
            builder.Configuration.AddJsonFile(explicitPaperJson, optional: false, reloadOnChange: true);
        }
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
builder.Services.AddScoped<ITradeRepository, TradeRepository>();
builder.Services.AddScoped<ReconciliationAgent>();
// Cross-process operator alert publisher — INSERTs into OperatorAlertEvents,
// Listener.Api's OperatorAlertDbReaderWorker republishes via SignalR.
builder.Services.AddSingleton<IOperatorAlertPublisher, DbOperatorAlertPublisher>();
// Phase 13 — heartbeat for the System Health page in the dashboard
builder.Services.AddHostedService<ServiceHeartbeatHostedService>();

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
        // Shadow execution audit — every 60min, re-prices closed trades through square-root impact
        // model + spread + commission and compares to paper PnL. Tells us if alpha is decaying
        // because EDGE is failing or EXECUTION is failing.
        builder.Services.AddHostedService<ShadowAuditLiveHostedService>();
        // Portfolio risk analytics — every 15min publishes Kelly recommendation + VaR/CVaR.
        // Alerts when tail risk exceeds % of basis. Read-only.
        builder.Services.AddHostedService<PortfolioRiskLiveHostedService>();
        // Daily 23:55 UTC paper→live promotion gate verdict (objective criteria, not gut feel).
        builder.Services.AddHostedService<PromotionGateLiveHostedService>();
        Log.Information(
            "QuantEngine.Worker: TMQ_MODE=paper. Hosted services: ExchangeBalanceSync, PaperTrading (directional disabled), Pairs, FundingMirror+FundingHybrid, PairsDiscovery, TcaLiveAnalyzer, ShadowAuditLive, PortfolioRiskLive, PromotionGateLive, DailyTelegramDigest.");
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
