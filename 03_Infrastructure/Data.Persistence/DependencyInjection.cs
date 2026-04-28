using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Options;
using ThatsMyQuant.Infrastructure.Persistence;
using ThatsMyQuant.Infrastructure.Services.Foundation;
using ThatsMyQuant.Infrastructure.Services.Patterns;
using ThatsMyQuant.Infrastructure.Services.Strategy;
using ThatsMyQuant.Infrastructure.Services.Risk;
using ThatsMyQuant.Infrastructure.Services.MetaLearning;
using ThatsMyQuant.Infrastructure.Services;

namespace ThatsMyQuant.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddThatsMyQuantInfrastructure(this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        if (configuration != null)
            services.Configure<TelegramOptions>(configuration.GetSection("Telegram"));
        services.AddDbContextFactory<QuantDbContext>(opt => opt.UseConfiguredQuantDatabase(configuration ?? new ConfigurationBuilder().Build()));
        services.AddSingleton<IAuditLogger, DbAuditLogger>();
        services.AddSingleton<IAlertHistoryStore, DbAlertHistoryStore>();
        // BIST gateway — only the simulated stub exists today. Behind a feature flag so
        // production Crypto-only runs don't instantiate it. Default when no config is
        // bound is TRUE to preserve legacy test-harness wiring; when config IS bound,
        // the explicit Markets:EnableBist value wins (default false — crypto-only).
        var bistEnabled = configuration is null
            || (bool.TryParse(configuration["Markets:EnableBist"], out var bistFlag) && bistFlag);
        if (bistEnabled)
        {
            services.AddSingleton<IBistMarketGateway, SimulatedBistMarketGateway>();
        }

        services.AddSingleton<VenuePortfolioManager>();
        services.AddSingleton<ISharedPortfolioStateManager>(sp => sp.GetRequiredService<VenuePortfolioManager>());
        services.AddSingleton<IPortfolioManager>(sp => sp.GetRequiredService<VenuePortfolioManager>());

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
        // 2026-04-25: Directional single-symbol agents (Momentum/Breakout/StatArb/Scalping)
        // are gated behind Strategy:EnableDirectionalAgents (default FALSE). Empirical results
        // on crypto 1h showed WR 25-34% across 5 symbols — structurally losing on this regime.
        // Pairs + funding-alpha strategies remain the validated edge. Flip to true to re-enable
        // for non-crypto markets (BIST, Forex, longer timeframes) where directional may work.
        var directionalEnabled = configuration is not null
            && bool.TryParse(configuration["Strategy:EnableDirectionalAgents"], out var dirFlag)
            && dirFlag;
        if (directionalEnabled)
        {
            services.AddScoped<IStrategyAgent, MomentumStrategyAgent>();
            services.AddScoped<IStrategyAgent, BreakoutStrategyAgent>();
            services.AddScoped<IStrategyAgent, StatisticalArbitrageAgent>();
            services.AddScoped<IStrategyAgent, ScalpingStrategyAgent>();
        }

        // Tier 4: Risk
        services.AddScoped<IRiskGuardAgent, KellyPositionSizerAgent>();
        services.AddScoped<IRiskGuardAgent, PortfolioOptimiserAgent>();
        services.AddScoped<IRiskGuardAgent, CorrelationGuardAgent>();

        // Tier 5: Meta-Learning (Singletons to maintain rolling state)
        services.AddSingleton<IMetaLearningAgent, RegimeSwitchingModelAgent>();
        services.AddSingleton<IMetaLearningAgent, OnlineLearningAdaptorAgent>();
        services.AddSingleton<IMetaLearningAgent, ReinforcementLearningAgent>();

        // Exchange: Multi-exchange routing
        services.AddSingleton<ExchangeServiceResolver>();
        services.AddSingleton<IExchangeServiceResolver>(sp => sp.GetRequiredService<ExchangeServiceResolver>());

        services.AddHttpClient<IHistoricalMarketDataService, OkxHistoricalDataService>();
        // 2026-04-24: Scoped (not Singleton). QuantIndicatorService depends on the scoped
        // graph (ExitPolicyLearningService, MultiTimeframeConfluenceService, etc.) — Singleton
        // caused captive-dependency violations. TA analysis is per-request anyway (per bar /
        // per signal), Scoped lifetime matches the usage. Revert to Singleton/routers stays OK.
        services.AddScoped<ITechnicalAnalysisService, QuantIndicatorService>();

        // Named HTTP client for OKX public endpoints (funding, fees, symbol profiles, etc).
        // Prior bug: CLI didn't register this named client, so provider hit relative URIs → empty.
        services.AddHttpClient("OkxPublic", c =>
        {
            c.BaseAddress = new Uri("https://www.okx.com");
            c.Timeout = TimeSpan.FromSeconds(30);
        });

        // Real-cost providers — replace hardcoded BacktestCostModel defaults with OKX live data.
        services.AddSingleton<ISymbolCostProfileProvider, OkxSymbolCostProfileProvider>();
        services.AddSingleton<IFundingRateProvider, OkxFundingRateProvider>();

        // Real execution cost snapshot cache: fee + live spread with HTTP fallback.
        // Uses a lightweight static fee provider wrapped over config (no API keys) so
        // this works end-to-end in backtest/paper without exchange credentials.
        services.AddHttpClient();
        services.AddSingleton<IVenueTradeFeeProvider, ConfigDrivenVenueFeeProvider>();
        services.AddSingleton<IExecutionCostSnapshotCache, ExecutionCostSnapshotCache>();

        return services;
    }
}
