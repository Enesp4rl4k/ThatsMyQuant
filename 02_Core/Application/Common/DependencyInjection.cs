using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Observability;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.HftInfrastructure;
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
        services.AddSingleton(new ServiceRuntimeState());
        services.AddSingleton(new OperationalTelemetry());

        // Application Layer Services - Singletons (Stateful/Heavy)
        services.AddSingleton<RiskAgent>();
        services.AddSingleton<InferenceAgent>();
        services.AddSingleton<FeatureEngineeringAgent>();
        services.AddSingleton<MemoryPortfolioManager>();
        services.AddSingleton<IPortfolioManager>(sp => sp.GetRequiredService<MemoryPortfolioManager>());
        services.AddSingleton<IPortfolioStateCache, PortfolioStateCache>();
        services.AddSingleton<NewsFeedSim>();
        services.AddSingleton<IStrategyPerformanceStore, JsonStrategyPerformanceStore>();
        services.AddSingleton<IStrategyTuningStateStore, JsonStrategyTuningStateStore>();
        services.AddSingleton<StrategyTuningService>();
        services.AddSingleton<MarketExecutionProfileService>();
        services.AddSingleton<TickPipeline>();
        services.AddSingleton<CircuitBreakerService>();
        services.AddSingleton<ModelTrainingPipeline>();
        services.AddSingleton<ModelExperimentService>();
        services.AddSingleton<ExecutionSignalQueue>();
        services.AddSingleton(sp => new FeatureDriftMonitor(
            featureCount: 5,
            logger: sp.GetService<ILogger<FeatureDriftMonitor>>()));
        services.AddSingleton<DeadMansSwitch>();
        services.AddSingleton<HeartbeatMonitor>();
        services.AddSingleton<MultiTimeframeConfluenceService>();
        services.AddSingleton<PortfolioRebalancingScheduler>();
        services.AddHostedService<ThatsMyQuant.Application.Risk.RiskSupervisorService>();

        // Application Layer Services - Scoped (Logic/Handlers/LLM-Dependent)
        services.AddScoped<SentimentAgent>();
        services.AddScoped<SwarmOrchestrator>();
        services.AddScoped<AdaptiveExecutionOrchestrator>();
        services.AddScoped<StrategyExpectancyService>();
        services.AddScoped<SymbolExpectancyService>();
        services.AddScoped<EdgeScoreService>();
        services.AddScoped<StrategyPromotionService>();
        services.AddScoped<ExitPolicyLearningService>();
        services.AddScoped<PositionSupervisorService>();
        services.AddScoped<LivePositionLifecycleService>();
        services.AddScoped<TradeExecutionSyncService>();
        services.AddScoped<HftExecutionService>();
        services.AddScoped<TickReplayEngine>();
        services.AddScoped<DynamicSymbolUniverseService>();
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
