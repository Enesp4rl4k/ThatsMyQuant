using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.HftInfrastructure;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Common;

public static class DependencyInjection
{
    public static IServiceCollection AddThatsMyQuantApplication(this IServiceCollection services)
    {
        services.AddOptions();
        services.AddOptions<RiskGovernorOptions>();
        services.AddOptions<VpinOptions>();
        services.AddOptions<MonteCarloOptions>();
        services.AddOptions<SwarmOptions>();
        services.AddOptions<ExecutionOptions>();
        services.AddOptions<RegimeOptions>();
        services.AddOptions<FeatureOptions>();
        services.AddOptions<WebSocketOptions>();
        services.AddOptions<TradingCoreOptions>();
        services.AddOptions<MarketRegimeDetectionOptions>();
        services.AddOptions<MachineLearningOptions>();
        services.AddOptions<StrategyLearningOptions>();
        services.AddOptions<ArbitrageOptions>();
        services.AddOptions<ResilienceOptions>();
        services.AddOptions<ExchangeUrlOptions>();
        services.AddOptions<MetaLearningAgentOptions>();
        services.AddOptions<RiskGuardAgentOptions>();
        services.AddOptions<PatternAgentOptions>();

        // ═══════════════════════════════════════════════════════════════
        // 3-TIER HFT INFRASTRUCTURE (Red/Yellow/Blue Path backbone)
        // ═══════════════════════════════════════════════════════════════
        services.AddSingleton<HotStateBuffer>();             // Shared atomic state (Red↔Yellow↔Blue)
        services.AddSingleton<MonteCarloSimulator>();         // Blue Path simulator

        // Application Layer Services - Singletons (Stateful/Heavy)
        services.AddSingleton<RiskAgent>();
        services.AddSingleton<InferenceAgent>();
        services.AddSingleton<FeatureEngineeringAgent>();
        services.AddSingleton<MemoryPortfolioManager>();
        services.AddSingleton<VpinCalculator>();
        services.AddSingleton<AdaptiveLimitOrderEngine>();
        services.AddSingleton<IPortfolioStateCache, PortfolioStateCache>();
        services.AddSingleton<NewsFeedSim>();
        services.AddScoped<IMarketExecutionProfileService, MarketExecutionProfileService>();
        services.AddSingleton<TickPipeline>();
        services.AddSingleton<TickReplayEngine>();
        services.AddSingleton<CircuitBreakerService>();
        services.AddSingleton<ModelTrainingPipeline>();
        services.AddSingleton(sp =>
        {
            var featureOpts = sp.GetService<Microsoft.Extensions.Options.IOptions<FeatureOptions>>()?.Value ?? new FeatureOptions();
            return new FeatureDriftMonitor(
                featureCount: featureOpts.FeatureCount,
                maxWindowSize: featureOpts.DriftMonitorWindowSize,
                logger: sp.GetService<ILogger<FeatureDriftMonitor>>());
        });

        // Blue Path: Cold analytics background service
        services.AddHostedService<ColdPathAnalyticsService>();

        // Application Layer Services - Scoped (Logic/Handlers/LLM-Dependent)
        services.AddScoped<SentimentAgent>();
        services.AddScoped<SwarmOrchestrator>();
        services.AddScoped<StrategyExpectancyService>();
        services.AddScoped<SymbolExpectancyService>();
        services.AddScoped<EdgeScoreService>();
        services.AddScoped<StrategyPromotionService>();
        services.AddScoped<ExitPolicyLearningService>();
        services.AddScoped<PositionSupervisorService>();
        services.AddScoped<HftExecutionService>();
        services.AddScoped<IHftMarketDataService, HftMarketDataService>();
        services.AddScoped<IHftSignalGenerationService, HftSignalGenerationService>();
        services.AddScoped<OFIExecutionAgent>();
        
        services.AddScoped<IStrategyRouter, HftStrategyRouter>();
        services.AddScoped<IStrategyRouter, TrendStrategyRouter>();
        services.AddScoped<IStrategyRouter, RangeStrategyRouter>();
        services.AddScoped<IStrategyRouter, BreakoutStrategyRouter>();

        // 5-Tier Agent Hierarchy Orchestrator
        services.AddScoped<IAgentCoordinator, AgentCoordinator>();

        return services;
    }
}
