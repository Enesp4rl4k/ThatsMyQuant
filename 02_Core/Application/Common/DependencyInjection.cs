using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Common;

public static class DependencyInjection
{
    public static IServiceCollection AddThatsMyQuantApplication(this IServiceCollection services, IConfiguration? configuration = null)
    {
        // Bind Trading options from configuration (or use defaults)
        var tradingSection = configuration?.GetSection(TradingOptions.SectionName);
        if (tradingSection?.Exists() == true)
        {
            services.Configure<RiskOptions>(tradingSection.GetSection(nameof(TradingOptions.Risk)));
            services.Configure<BacktestOptions>(tradingSection.GetSection(nameof(TradingOptions.Backtest)));
            services.Configure<SwarmOptions>(tradingSection.GetSection(nameof(TradingOptions.Swarm)));
            services.Configure<MomentumOptions>(tradingSection.GetSection(nameof(TradingOptions.Momentum)));
            services.Configure<BreakoutOptions>(tradingSection.GetSection(nameof(TradingOptions.Breakout)));
            services.Configure<ScalpingOptions>(tradingSection.GetSection(nameof(TradingOptions.Scalping)));
            services.Configure<StatArbOptions>(tradingSection.GetSection(nameof(TradingOptions.StatArb)));
            services.Configure<VolatilityExpansionOptions>(tradingSection.GetSection(nameof(TradingOptions.VolatilityExpansion)));
            services.Configure<MlOptions>(tradingSection.GetSection(nameof(TradingOptions.Ml)));
            services.Configure<TickReplayOptions>(tradingSection.GetSection(nameof(TradingOptions.TickReplay)));
        }
        else
        {
            // Register default options so IOptions<T> resolves even without config
            services.Configure<RiskOptions>(_ => { });
            services.Configure<BacktestOptions>(_ => { });
            services.Configure<SwarmOptions>(_ => { });
            services.Configure<MomentumOptions>(_ => { });
            services.Configure<BreakoutOptions>(_ => { });
            services.Configure<ScalpingOptions>(_ => { });
            services.Configure<StatArbOptions>(_ => { });
            services.Configure<VolatilityExpansionOptions>(_ => { });
            services.Configure<MlOptions>(_ => { });
            services.Configure<TickReplayOptions>(_ => { });
        }

        // WebSocket options
        var wsSection = configuration?.GetSection(WebSocketOptions.SectionName);
        if (wsSection?.Exists() == true)
            services.Configure<WebSocketOptions>(wsSection);
        else
            services.Configure<WebSocketOptions>(_ => { });

        // Application Layer Services - Singletons (Stateful/Heavy)
        services.AddSingleton<RiskAgent>();
        services.AddSingleton<InferenceAgent>();
        services.AddSingleton<FeatureEngineeringAgent>();
        services.AddSingleton<FeatureDriftMonitor>();
        services.AddSingleton<MemoryPortfolioManager>();
        services.AddSingleton<IPortfolioStateCache, PortfolioStateCache>();
        services.AddSingleton<NewsFeedSim>();
        services.AddSingleton<IStrategyPerformanceStore, JsonStrategyPerformanceStore>();
        services.AddSingleton<MarketExecutionProfileService>();
        services.AddSingleton<DataFreshnessMonitor>();
        services.AddSingleton<TickPipeline>();
        services.AddScoped<TickReplayEngine>();
        services.AddScoped<BacktestEngineApp>();
        services.AddScoped<WalkForwardEngine>();
        services.AddSingleton<BacktestResultStore>();

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
        services.AddScoped<IAgentCoordinator, AgentCoordinator>();

        return services;
    }
}
