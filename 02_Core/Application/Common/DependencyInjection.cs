using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Observability;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.HftInfrastructure;
using ThatsMyQuant.Application.Risk;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Microsoft.Extensions.Options;

namespace ThatsMyQuant.Application.Common;

public static class DependencyInjection
{
    public static IServiceCollection AddThatsMyQuantApplication(this IServiceCollection services)
    {
        services.AddOptions();
        services.AddOptions<RiskGovernorOptions>();
        services.AddOptions<OrderBookExecutionOptions>().BindConfiguration("Execution:OrderBook");
        services.AddOptions<PaperLeverageOptions>().BindConfiguration("Paper:Leverage");
        services.AddOptions<PaperExecutionRealismOptions>().BindConfiguration("Paper:Realism");
        services.AddOptions<PaperPortfolioLimitsOptions>().BindConfiguration("Paper:PortfolioLimits");
        services.AddOptions<MarketUniverseOptions>().BindConfiguration("Markets");
        services.AddOptions<BacktestWalkForwardGateOptions>().BindConfiguration("Backtest:WalkForwardGate");
        services.AddOptions<BacktestTimeframeProfileOptions>().BindConfiguration("Backtest:Profiles");
        services.AddOptions<BacktestQualityGateOptions>().BindConfiguration("Backtest:QualityGate");
        services.AddOptions<BacktestGuardOptions>().BindConfiguration("Backtest:Guards");
        services.AddOptions<PairsTradingOptions>().BindConfiguration("Strategy:PairsTrading");
        services.AddOptions<ProfitMaximizationOptions>().BindConfiguration("Backtest:ProfitMax");
        services.AddOptions<BacktestParityOptions>().BindConfiguration("Backtest:Parity");
        services.AddOptions<BacktestPerformanceOptions>().BindConfiguration("Backtest:Performance");
        services.AddOptions<OrderExecutionOptions>().BindConfiguration(OrderExecutionOptions.SectionName);
        services.AddOptions<SlippageAlertOptions>().BindConfiguration(SlippageAlertOptions.SectionName);
        services.TryAddSingleton<IAuditLogger, NullAuditLogger>();
        services.AddSingleton(new ServiceRuntimeState());
        services.AddSingleton(new OperationalTelemetry());

        // Application Layer Services - Singletons (Stateful/Heavy)
        // Cross-strategy evaluation log — strategies push reject/enter/hold reasons
        // here on every tick; web UI surfaces them via /api/strategy/evaluations.
        services.AddSingleton<StrategyEvaluationLog>();
        services.AddSingleton<RiskAgent>();
        services.AddSingleton<VolatilityTargetingService>();
        services.AddSingleton(sp => new FeatureDriftMonitor(
            featureCount: 5,
            logger: sp.GetService<ILogger<FeatureDriftMonitor>>()));
        services.AddSingleton(sp =>
        {
            var opts = BindInferenceOptions(sp.GetService<IConfiguration>());
            var cfg = sp.GetService<IConfiguration>();
            var modelPath = cfg?["Inference:ModelPath"];
            if (string.IsNullOrWhiteSpace(modelPath))
                modelPath = "PredictiveAlphaModel.zip";
            return new InferenceAgent(
                sp.GetService<FeatureDriftMonitor>(),
                modelPath,
                opts,
                sp.GetService<ILogger<InferenceAgent>>(),
                sp.GetService<OperationalTelemetry>());
        });
        services.AddSingleton<FeatureEngineeringAgent>();
        services.AddSingleton<MemoryPortfolioManager>();
        services.TryAddSingleton<IPortfolioManager>(sp => sp.GetRequiredService<MemoryPortfolioManager>());
        services.AddSingleton<IPortfolioStateCache, PortfolioStateCache>();
        services.AddSingleton<NewsFeedSim>();
        services.AddSingleton<IStrategyPerformanceStore, JsonStrategyPerformanceStore>();
        services.AddSingleton<PaperPortfolioPersistenceService>();
        // Shared paper portfolio — all paper hosted services (directional + pairs + funding)
        // mutate the same instance so cash, open positions and PnL stay unified.
        services.AddSingleton<PaperPortfolio>();
        services.AddSingleton<IStrategyTuningStateStore, JsonStrategyTuningStateStore>();
        services.AddSingleton<StrategyTuningService>();
        services.AddSingleton<IExecutionCostSnapshotCache, NullExecutionCostSnapshotCache>();
        services.AddSingleton(sp => new MarketExecutionProfileService(sp.GetRequiredService<IExecutionCostSnapshotCache>()));
        services.AddSingleton<TickPipeline>();
        services.AddSingleton<CircuitBreakerService>();
        services.AddSingleton<ModelTrainingPipeline>();
        services.AddSingleton<ModelExperimentService>();
        services.AddSingleton<ExecutionSignalQueue>();
        services.AddSingleton<DeadMansSwitch>();
        services.AddSingleton<HeartbeatMonitor>();
        services.AddHostedService<HeartbeatWatchdogHostedService>();
        services.AddHostedService<ReconciliationHostedService>();
        // 2026-05-01: TelegramReadBotHostedService removed — duplicate of QuantEngine.Worker's
        // TelegramCommandHostedService. AddThatsMyQuantApplication() is called from 5 hosts
        // (CLI, ExecutionWorker, Listener.Api, QuantWorker, Dashboard); registering a long-poll
        // bot here multiplied getUpdates pollers by ~5, producing the HTTP 409 conflict storm
        // (Telegram allows only one poller per bot token). The Worker-tier
        // TelegramCommandHostedService is the canonical bot — it covers /status /pnl /positions
        // /help PLUS /digest (on-demand digest trigger). Single registration, single poller.
        services.AddSingleton<ThatsMyQuant.Application.Observability.IExchangeHealthProvider, ThatsMyQuant.Application.Observability.ExchangeHealthProvider>();
        services.AddHostedService<ThatsMyQuant.Application.Observability.ExchangeHealthProbeHostedService>();
        services.AddSingleton<MultiTimeframeConfluenceService>();
        services.AddSingleton<PortfolioRebalancingScheduler>();
        services.AddHostedService(sp =>
        {
            var config = sp.GetService<IConfiguration>();
            var intervalMs = 1000;
            if (config != null && int.TryParse(config["Risk:Supervisor:MonitoringIntervalMs"], out var parsed))
                intervalMs = Math.Clamp(parsed, 100, 300_000);
            var maxDd = 5.0m;
            if (config != null && decimal.TryParse(config["Risk:Supervisor:MaxDrawdownPercent"], NumberStyles.Any, CultureInfo.InvariantCulture, out var dd))
                maxDd = dd;
            return new RiskSupervisorService(
                sp.GetRequiredService<ILogger<RiskSupervisorService>>(),
                sp.GetRequiredService<IExchangeService>(),
                sp.GetRequiredService<IPortfolioManager>(),
                maxDrawdownPercent: maxDd,
                monitoringInterval: TimeSpan.FromMilliseconds(intervalMs));
        });

        // Application Layer Services - Scoped (Logic/Handlers/LLM-Dependent)
        services.AddScoped<SentimentAgent>();
        services.AddScoped<SwarmOrchestrator>();
        services.AddScoped<AdaptiveExecutionOrchestrator>();
        services.AddScoped<StrategyExpectancyService>();
        services.AddScoped<SymbolExpectancyService>();
        services.AddScoped<EdgeScoreService>();
        services.AddScoped<ProfitLeakAuditService>();
        // Singleton: performance state must survive across ProcessSignalHandler invocations; InitializeAsync loads JSON once at host startup.
        services.AddSingleton<StrategyPromotionService>();
        services.AddScoped<ExitPolicyLearningService>();
        services.AddScoped(sp =>
        {
            var cfg = sp.GetService<IConfiguration>();
            var tf = cfg?["Paper:Timeframe"] ?? cfg?["TMQ_PAPER_TIMEFRAME"] ?? "15m";
            return new PaperTradingService(
                sp.GetRequiredService<ITechnicalAnalysisService>(),
                sp.GetRequiredService<IMarketDataService>(),
                sp.GetRequiredService<ILogger<PaperTradingService>>(),
                sp.GetService<StrategyPromotionService>(),
                sp.GetService<ExitPolicyLearningService>(),
                sp.GetService<MarketExecutionProfileService>(),
                sp.GetService<PositionSupervisorService>(),
                sp.GetService<IExecutionCostSnapshotCache>(),
                sp.GetService<IAgentCoordinator>(),
                sp.GetServices<ISignalValidator>(),
                tf,
                sp.GetService<IOptions<PaperLeverageOptions>>()?.Value,
                sp.GetService<BacktestCostModel>(),
                sp.GetService<IOptions<PaperExecutionRealismOptions>>()?.Value,
                sp.GetService<IOptions<PaperPortfolioLimitsOptions>>()?.Value,
                mediator: null,
                persistence: sp.GetService<PaperPortfolioPersistenceService>(),
                universeOptions: sp.GetService<IOptions<MarketUniverseOptions>>()?.Value,
                portfolio: sp.GetRequiredService<PaperPortfolio>());
        });
        services.AddScoped<PositionSupervisorService>();
        services.AddScoped<LivePositionLifecycleService>();
        services.AddScoped<TradeExecutionSyncService>();
        services.AddScoped<HftExecutionService>();
        services.AddScoped<TickReplayEngine>();
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetService<IConfiguration>();
            var section = cfg?.GetSection("Backtest:Costs");
            decimal commission = 5m, slippage = 2m, funding = 1m, makerCommission = 2m, makerOffset = 3m;
            bool useMaker = true;
            int makerTimeout = 2;
            if (section != null)
            {
                if (decimal.TryParse(section["CommissionBps"], NumberStyles.Any, CultureInfo.InvariantCulture, out var c))
                    commission = c;
                if (decimal.TryParse(section["SlippageBps"], NumberStyles.Any, CultureInfo.InvariantCulture, out var s))
                    slippage = s;
                if (decimal.TryParse(section["FundingBpsPer8h"], NumberStyles.Any, CultureInfo.InvariantCulture, out var f))
                    funding = f;
                if (decimal.TryParse(section["MakerCommissionBps"], NumberStyles.Any, CultureInfo.InvariantCulture, out var mc))
                    makerCommission = mc;
                if (decimal.TryParse(section["MakerEntryOffsetBps"], NumberStyles.Any, CultureInfo.InvariantCulture, out var mo))
                    makerOffset = mo;
                if (bool.TryParse(section["UseMakerEntries"], out var um))
                    useMaker = um;
                if (int.TryParse(section["MakerEntryTimeoutBars"], out var mt))
                    makerTimeout = mt;
            }
            return new BacktestCostModel
            {
                CommissionBps = commission,
                SlippageBps = slippage,
                FundingBpsPer8h = funding,
                MakerCommissionBps = makerCommission,
                MakerEntryOffsetBps = makerOffset,
                UseMakerEntries = useMaker,
                MakerEntryTimeoutBars = makerTimeout
            };
        });
        services.AddScoped<BacktestEngineApp>();
        services.AddScoped<BacktestOrchestrator>();
        services.AddScoped<PairsBacktestRunner>();
        services.AddScoped<FundingAlphaBacktestRunner>();
        services.AddScoped<PortfolioBacktestRunner>();
        // Pro Mode (Level 2.5) — all singletons so state persists across backtests/refreshes.
        services.AddSingleton<ThatsMyQuant.Application.Strategy.ProMode.CircuitBreakerMonitor>();
        services.AddSingleton<ThatsMyQuant.Application.Strategy.ProMode.CandidatePool>();
        services.AddSingleton<ThatsMyQuant.Application.Strategy.ProMode.FundingOpportunityScanner>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ThatsMyQuant.Application.Strategy.ProMode.FundingOpportunityScanner>>();
            var provider = sp.GetRequiredService<ThatsMyQuant.Contracts.Interfaces.IFundingRateProvider>();
            return new ThatsMyQuant.Application.Strategy.ProMode.FundingOpportunityScanner(logger, provider)
            {
                Watchlist = new[]
                {
                    "BTC-USDT","ETH-USDT","SOL-USDT","AVAX-USDT","LINK-USDT",
                    "LTC-USDT","DOGE-USDT","XRP-USDT","ADA-USDT","AAVE-USDT",
                    "ATOM-USDT","DOT-USDT","NEAR-USDT","APT-USDT","SUI-USDT",
                    "ARB-USDT","OP-USDT","INJ-USDT","SHIB-USDT","FIL-USDT",
                    "TRX-USDT","XLM-USDT","BCH-USDT","UNI-USDT","ETC-USDT"
                }
            };
        });
        services.AddSingleton<ThatsMyQuant.Application.Strategy.ProMode.ProModeOrchestrator>();
        services.AddSingleton<ThatsMyQuant.Application.Strategy.ProMode.CorrelationBreakGuard>();
        services.AddSingleton<ThatsMyQuant.Application.Strategy.ProMode.ConcentrationGuard>();
        services.AddScoped<ThatsMyQuant.Application.Strategy.ProMode.SystemBehaviorSimulator>();
        services.AddScoped<ThatsMyQuant.Application.Strategy.ProMode.StressScenarioSimulator>();
        // Operator alerts — log-only by default (paper safe). For live, replace registration
        // with TelegramOperatorAlertService and supply OperatorAlerts:Telegram:BotToken + ChatId.
        services.AddSingleton<ThatsMyQuant.Application.Strategy.ProMode.IOperatorAlertService,
            ThatsMyQuant.Application.Strategy.ProMode.LogOnlyOperatorAlertService>();
        services.AddScoped<ThatsMyQuant.Application.Strategy.ProMode.AllocationPipeline>();
        // MediatR handler — flattens portfolio + alerts on circuit breaker trip.
        services.AddTransient<MediatR.INotificationHandler<ThatsMyQuant.Application.Strategy.ProMode.CircuitBreakerTrippedNotification>,
            ThatsMyQuant.Application.Strategy.ProMode.CircuitBreakerFlattenHandler>();
        services.AddScoped<PairsDiscoveryService>();
        services.AddSingleton<PairsPortfolioCoordinator>();
        services.AddSingleton<IPairPortfolioDiagnostics>(sp => sp.GetRequiredService<PairsPortfolioCoordinator>());
        services.AddScoped<WalkForwardEngine>();
        services.AddScoped<WalkForwardOptimizer>();
        services.AddSingleton<RegimeExpectancyService>();
        services.TryAddSingleton<IFundingRateProvider, NullFundingRateProvider>();
        services.AddSingleton<FundingRateSignalService>();
        services.AddScoped<DynamicSymbolUniverseService>();
        services.AddScoped<IHftMarketDataService, HftMarketDataService>();
        services.AddScoped<IHftSignalGenerationService, HftSignalGenerationService>();
        services.AddScoped<OFIExecutionAgent>();
        
        // 2026-04-24: Singleton (not Scoped). Strategy routers are stateless — they route
        // a TradeDecision based on regime + signal without per-request state. Scoped caused
        // captive-dependency violation under Singleton QuantIndicatorService consumer.
        services.AddSingleton<IStrategyRouter, HftStrategyRouter>();
        services.AddSingleton<IStrategyRouter, TrendStrategyRouter>();
        services.AddSingleton<IStrategyRouter, RangeStrategyRouter>();
        services.AddSingleton<IStrategyRouter, BreakoutStrategyRouter>();

        // 5-Tier Agent Hierarchy Orchestrator
        services.AddScoped<IAgentCoordinator, AgentCoordinator>();

        // Analytics — signal-quality audit (Information Coefficient, signal-alone PnL).
        services.AddScoped<ThatsMyQuant.Application.Analytics.SignalQualityAuditService>();

        // Pro-level analytics + lifecycle services (2026-04-24 Phase 3).
        services.AddScoped<ThatsMyQuant.Application.Analytics.TransactionCostAnalysisService>();
        services.AddScoped<ThatsMyQuant.Application.Analytics.MonteCarloBootstrapService>();
        services.AddSingleton<ThatsMyQuant.Application.Analytics.ExecutionParityService>();
        services.AddScoped<FormalPromotionGate>();
        // Shadow execution audit — pre-promotion sanity check that re-walks backtest
        // trades through the square-root impact model + spread + commission and reports
        // whether the headline PnL survives realistic fills.
        services.AddSingleton<IShadowExecutionAuditService, ShadowExecutionAuditService>();
        services.AddSingleton<ModelRetrainOrchestrator>();
        services.AddHostedService<ThatsMyQuant.Application.MachineLearning.ModelRetrainHostedService>();
        // 2026-04-24: Singleton (not Scoped). Stateless — only ctor dep is ILogger.
        // Was causing captive-dependency violation when consumed by Singleton RiskAgent
        // (detected by ASP.NET Core Development-mode DI validation).
        services.AddSingleton<ThatsMyQuant.Application.Risk.PortfolioRiskAnalytics>();
        services.AddSingleton<ChampionChallengerService>();

        return services;
    }

    private static InferenceOptions BindInferenceOptions(IConfiguration? config)
    {
        var o = new InferenceOptions();
        if (config == null)
            return o;
        var s = config.GetSection("Inference");
        if (!s.Exists())
            return o;
        if (int.TryParse(s["PredictionEngineCount"], out var pec))
            o.PredictionEngineCount = pec;
        if (int.TryParse(s["MaxPredictionEngines"], out var mpe))
            o.MaxPredictionEngines = mpe;
        if (bool.TryParse(s["UseParallelWarmup"], out var upw))
            o.UseParallelWarmup = upw;
        if (bool.TryParse(s["LazyPoolWarmup"], out var lazy))
            o.LazyPoolWarmup = lazy;
        if (!string.IsNullOrWhiteSpace(s["LazyPoolBackgroundLogLevel"]))
            o.LazyPoolBackgroundLogLevel = s["LazyPoolBackgroundLogLevel"]!.Trim();
        return o;
    }
}
