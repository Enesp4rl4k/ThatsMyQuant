using Microsoft.Extensions.DependencyInjection;
using ThatsMyQuant.Application.Strategy;

namespace ThatsMyQuant.Application.Common;

public static class DependencyInjection
{
    public static IServiceCollection AddThatsMyQuantApplication(this IServiceCollection services)
    {
        // Application Layer Services (Logic-only)
        services.AddSingleton<RiskAgent>();
        services.AddSingleton<StrategyExpectancyService>();
        services.AddSingleton<SymbolExpectancyService>();
        services.AddSingleton<EdgeScoreService>();
        services.AddSingleton<IStrategyPerformanceStore, JsonStrategyPerformanceStore>();
        services.AddSingleton<StrategyPromotionService>();
        services.AddSingleton<ExitPolicyLearningService>();
        services.AddSingleton<MarketExecutionProfileService>();
        services.AddSingleton<HftExecutionService>();
        services.AddSingleton<IHftMarketDataService, HftMarketDataService>();
        services.AddSingleton<IHftSignalGenerationService, HftSignalGenerationService>();
        services.AddSingleton<SentimentAgent>();
        services.AddSingleton<SwarmOrchestrator>();
        services.AddSingleton<NewsFeedSim>();
        
        return services;
    }
}
