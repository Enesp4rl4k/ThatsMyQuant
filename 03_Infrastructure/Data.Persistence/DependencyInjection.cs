using Microsoft.Extensions.DependencyInjection;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Services.Foundation;
using ThatsMyQuant.Infrastructure.Services.Patterns;
using ThatsMyQuant.Infrastructure.Services.Strategy;
using ThatsMyQuant.Infrastructure.Services.Risk;
using ThatsMyQuant.Infrastructure.Services.MetaLearning;

namespace ThatsMyQuant.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddThatsMyQuantInfrastructure(this IServiceCollection services)
    {
        // Tier 1: Foundation
        services.AddScoped<IFoundationAgent, OrderBookDepthAnalyzer>();
        services.AddScoped<IFoundationAgent, VolumeProfileAgent>();
        services.AddScoped<IFoundationAgent, MicrostructureAgent>();

        // Tier 2: Patterns
        services.AddScoped<IPatternAgent, VwapDeviationAgent>();
        services.AddScoped<IPatternAgent, FibonacciAgent>();
        services.AddScoped<IPatternAgent, IchimokuAgent>();
        services.AddScoped<IPatternAgent, BollingerBandAgent>();

        // Tier 3: Strategy
        services.AddScoped<IStrategyAgent, MomentumStrategyAgent>();
        services.AddScoped<IStrategyAgent, BreakoutStrategyAgent>();
        services.AddScoped<IStrategyAgent, StatisticalArbitrageAgent>();
        services.AddScoped<IStrategyAgent, ScalpingStrategyAgent>();
        services.AddScoped<IStrategyAgent, VolatilityExpansionAgent>();

        // Tier 4: Risk
        services.AddScoped<IRiskGuardAgent, KellyPositionSizerAgent>();
        services.AddScoped<IRiskGuardAgent, PortfolioOptimiserAgent>();
        services.AddScoped<IRiskGuardAgent, CorrelationGuardAgent>();

        // Tier 5: Meta-Learning (Singletons to maintain rolling state)
        services.AddSingleton<IMetaLearningAgent, RegimeSwitchingModelAgent>();
        services.AddSingleton<IMetaLearningAgent, OnlineLearningAdaptorAgent>();
        services.AddSingleton<IMetaLearningAgent, ReinforcementLearningAgent>();

        return services;
    }
}
