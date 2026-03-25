using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Services.Foundation;
using ThatsMyQuant.Infrastructure.Services.Patterns;
using ThatsMyQuant.Infrastructure.Services.Strategy;
using ThatsMyQuant.Infrastructure.Services.Risk;
using ThatsMyQuant.Infrastructure.Services.MetaLearning;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class FiveTierHierarchyIntegrationTests
{
    private readonly ILogger<AgentCoordinator> _coordinatorLogger = Substitute.For<ILogger<AgentCoordinator>>();
    private readonly ILogger<OrderBookDepthAnalyzer> _foundationLogger = Substitute.For<ILogger<OrderBookDepthAnalyzer>>();
    private readonly ILogger<VwapDeviationAgent> _patternLogger = Substitute.For<ILogger<VwapDeviationAgent>>();
    private readonly ILogger<MomentumStrategyAgent> _strategyLogger = Substitute.For<ILogger<MomentumStrategyAgent>>();
    private readonly ILogger<KellyPositionSizerAgent> _riskLogger = Substitute.For<ILogger<KellyPositionSizerAgent>>();
    private readonly ILogger<RegimeSwitchingModelAgent> _metaLogger = Substitute.For<ILogger<RegimeSwitchingModelAgent>>();

    [Fact]
    public async Task FullPipeline_ShouldGenerateSnapshotsAndVotes()
    {
        // 1. Arrange - Tier 1
        var foundationAgents = new List<IFoundationAgent>
        {
            new OrderBookDepthAnalyzer(_foundationLogger),
            new VolumeProfileAgent(Substitute.For<ILogger<VolumeProfileAgent>>()),
            new MicrostructureAgent(Substitute.For<ILogger<MicrostructureAgent>>())
        };

        // Arrange - Tier 2
        var patternAgents = new List<IPatternAgent>
        {
            new VwapDeviationAgent(_patternLogger),
            new IchimokuAgent(Substitute.For<ILogger<IchimokuAgent>>()),
            new BollingerBandAgent(Substitute.For<ILogger<BollingerBandAgent>>())
        };

        // Arrange - Tier 3
        var strategyAgents = new List<IStrategyAgent>
        {
            new MomentumStrategyAgent(_strategyLogger)
        };

        // Arrange - Tier 4
        var riskAgents = new List<IRiskGuardAgent>
        {
            new KellyPositionSizerAgent(_riskLogger)
        };

        // Arrange - Tier 5
        var metaAgents = new List<IMetaLearningAgent>
        {
            new RegimeSwitchingModelAgent(_metaLogger)
        };

        var coordinator = new AgentCoordinator(
            foundationAgents,
            patternAgents,
            strategyAgents,
            riskAgents,
            metaAgents,
            _coordinatorLogger);

        var signal = new Signal { Symbol = "BTCUSDT", Price = 50100m };
        var ticks = new List<MarketTick> 
        { 
            new MarketTick("BTCUSDT", 50090m, 1.0m, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false),
            new MarketTick("BTCUSDT", 50100m, 1.5m, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 100, false)
        };
        var candles = Enumerable.Range(0, 100).Select(i => new Candle 
        { 
            Close = 50000m + i, 
            High = 50100m + i, 
            Low = 49900m + i,
            Volume = i < 90 ? 1000m : 3000m // Stronger volume surge
        }).ToList();

        // 2. Act - Tier 1 & 2
        var foundation = await coordinator.BuildFoundationAsync(ticks, candles);
        var patterns = await coordinator.BuildPatternAsync(signal, foundation, candles);

        // 3. Act - Tier 3, 4, 5
        var votes = await coordinator.CollectStrategyVotesAsync(signal, patterns, foundation, candles, MarketRegime.Trending);
        var metaImpact = await coordinator.RunMetaAdaptationAsync(new List<Trade>(), MarketRegime.Trending);
        
        var decision = new TradeDecision { ShouldTrade = true, Confidence = 80 };
        var riskResult = await coordinator.RunRiskGauntletAsync(signal, decision, new List<Trade>());

        // 4. Assert
        foundation.Should().NotBeNull();
        patterns.Should().NotBeNull();
        votes.Should().NotBeEmpty();
        metaImpact.Should().NotBeNull();
        riskResult.Approved.Should().BeTrue();
        
        // Check tier-specific logic propagation
        votes[0].StrategyTag.Should().Be("Momentum");
        votes[0].ConfidenceAdjustment.Should().NotBe(0);
    }
}
