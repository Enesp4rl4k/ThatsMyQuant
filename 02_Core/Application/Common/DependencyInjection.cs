using Microsoft.Extensions.DependencyInjection;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Common;

public static class DependencyInjection
{
    public static IServiceCollection AddThatsMyQuantApplication(this IServiceCollection services)
    {
        // Application Layer Services - Singletons (Stateful/Heavy)
        services.AddSingleton<RiskAgent>();
        services.AddSingleton<InferenceAgent>();
        services.AddSingleton<FeatureEngineeringAgent>();
        services.AddSingleton<MemoryPortfolioManager>();
        services.AddSingleton<IPortfolioStateCache, PortfolioStateCache>();
        services.AddSingleton<NewsFeedSim>();
        services.AddSingleton<IStrategyPerformanceStore, JsonStrategyPerformanceStore>();
        services.AddSingleton<MarketExecutionProfileService>();
        services.AddSingleton<TickPipeline>();

        // Application Layer Services - Scoped (Logic/Handlers/LLM-Dependent)
        services.AddScoped<SentimentAgent>();
        services.AddScoped<SwarmOrchestrator>();
        services.AddScoped<StrategyExpectancyService>();
        services.AddScoped<SymbolExpectancyService>();
        services.AddScoped<EdgeScoreService>();
        services.AddScoped<StrategyPromotionService>();
        services.AddScoped<ExitPolicyLearningService>();
        services.AddScoped<HftExecutionService>();
        services.AddScoped<IHftMarketDataService, HftMarketDataService>();
        services.AddScoped<IHftSignalGenerationService, HftSignalGenerationService>();
        services.AddScoped<OFIExecutionAgent>();
        
        services.AddScoped<IStrategyRouter, HftStrategyRouter>();
        services.AddScoped<IStrategyRouter, TrendStrategyRouter>();
        services.AddScoped<IStrategyRouter, RangeStrategyRouter>();
        services.AddScoped<IStrategyRouter, BreakoutStrategyRouter>();

        // 5-Tier Agent Hierarchy Orchestrator
        services.AddScoped<AgentCoordinator>();

        return services;
    }
}
