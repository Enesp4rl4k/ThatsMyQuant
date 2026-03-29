using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

/// <summary>
/// Tests AgentCoordinator's RunSafe fallback behavior and partial agent failure handling.
/// Verifies that one failing agent doesn't bring down the entire pipeline.
/// </summary>
public sealed class AgentFailureTests
{
    private readonly ILogger<AgentCoordinator> _logger = Substitute.For<ILogger<AgentCoordinator>>();

    // ─── Foundation: single agent failure ────────────────────────────────

    [Fact]
    public async Task BuildFoundation_ReturnsEmpty_WhenAllAgentsFail()
    {
        var failAgent = Substitute.For<IFoundationAgent>();
        failAgent.AgentId.Returns("Fail.Foundation");
        failAgent.ComputeAsync(Arg.Any<IReadOnlyList<MarketTick>>(), Arg.Any<IReadOnlyList<Candle>>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Agent crashed"));

        var coordinator = new AgentCoordinator(
            new[] { failAgent },
            Array.Empty<IPatternAgent>(),
            Array.Empty<IStrategyAgent>(),
            Array.Empty<IRiskGuardAgent>(),
            Array.Empty<IMetaLearningAgent>(),
            _logger);

        var result = await coordinator.BuildFoundationAsync(new List<MarketTick>(), new List<Candle>());

        result.Should().Be(FoundationSnapshot.Empty);
    }

    [Fact]
    public async Task BuildFoundation_MergesHealthyAgent_WhenOneAgentFails()
    {
        var failAgent = Substitute.For<IFoundationAgent>();
        failAgent.AgentId.Returns("Fail.Agent");
        failAgent.ComputeAsync(Arg.Any<IReadOnlyList<MarketTick>>(), Arg.Any<IReadOnlyList<Candle>>(), Arg.Any<CancellationToken>())
            .Throws(new TimeoutException("Timed out"));

        var healthyAgent = Substitute.For<IFoundationAgent>();
        healthyAgent.AgentId.Returns("Healthy.Agent");
        healthyAgent.ComputeAsync(Arg.Any<IReadOnlyList<MarketTick>>(), Arg.Any<IReadOnlyList<Candle>>(), Arg.Any<CancellationToken>())
            .Returns(new FoundationSnapshot { BidAskImbalance = 0.3, Vwap = 100m });

        var coordinator = new AgentCoordinator(
            new[] { failAgent, healthyAgent },
            Array.Empty<IPatternAgent>(),
            Array.Empty<IStrategyAgent>(),
            Array.Empty<IRiskGuardAgent>(),
            Array.Empty<IMetaLearningAgent>(),
            _logger);

        var result = await coordinator.BuildFoundationAsync(new List<MarketTick>(), new List<Candle>());

        result.Should().NotBe(FoundationSnapshot.Empty);
        result.BidAskImbalance.Should().BeApproximately(0.3, 0.01);
    }

    // ─── Strategy votes: partial failure ─────────────────────────────────

    [Fact]
    public async Task CollectStrategyVotes_ReturnsAbstain_WhenAgentThrows()
    {
        var failStrategy = Substitute.For<IStrategyAgent>();
        failStrategy.StrategyTag.Returns("FailStrategy");
        failStrategy.CanHandle(Arg.Any<Signal>(), Arg.Any<MarketRegime>()).Returns(true);
        failStrategy.EvaluateAsync(Arg.Any<Signal>(), Arg.Any<PatternSnapshot>(), Arg.Any<FoundationSnapshot>(),
                Arg.Any<IReadOnlyList<Candle>>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Strategy crashed"));

        var coordinator = new AgentCoordinator(
            Array.Empty<IFoundationAgent>(),
            Array.Empty<IPatternAgent>(),
            new[] { failStrategy },
            Array.Empty<IRiskGuardAgent>(),
            Array.Empty<IMetaLearningAgent>(),
            _logger);

        var votes = await coordinator.CollectStrategyVotesAsync(
            new Signal { Symbol = "BTCUSDT" },
            PatternSnapshot.Empty,
            FoundationSnapshot.Empty,
            new List<Candle>(),
            MarketRegime.Ranging);

        votes.Should().HaveCount(1);
        votes[0].Vote.Should().Be(VoteOutcome.Abstain);
    }

    [Fact]
    public async Task CollectStrategyVotes_SkipsIneligibleAgents()
    {
        var ineligibleAgent = Substitute.For<IStrategyAgent>();
        ineligibleAgent.StrategyTag.Returns("Ineligible");
        ineligibleAgent.CanHandle(Arg.Any<Signal>(), Arg.Any<MarketRegime>()).Returns(false);

        var coordinator = new AgentCoordinator(
            Array.Empty<IFoundationAgent>(),
            Array.Empty<IPatternAgent>(),
            new[] { ineligibleAgent },
            Array.Empty<IRiskGuardAgent>(),
            Array.Empty<IMetaLearningAgent>(),
            _logger);

        var votes = await coordinator.CollectStrategyVotesAsync(
            new Signal { Symbol = "BTCUSDT" },
            PatternSnapshot.Empty,
            FoundationSnapshot.Empty,
            new List<Candle>(),
            MarketRegime.Trending);

        votes.Should().BeEmpty();
    }

    // ─── Risk gauntlet: guard failure pass-through ───────────────────────

    [Fact]
    public async Task RiskGauntlet_PassesThrough_WhenGuardThrows()
    {
        var failGuard = Substitute.For<IRiskGuardAgent>();
        failGuard.GuardId.Returns("FailGuard");
        failGuard.Order.Returns(1);
        failGuard.EvaluateAsync(Arg.Any<Signal>(), Arg.Any<TradeDecision>(),
                Arg.Any<IReadOnlyList<Trade>>(), Arg.Any<CancellationToken>())
            .Throws(new NullReferenceException("Guard null ref"));

        var coordinator = new AgentCoordinator(
            Array.Empty<IFoundationAgent>(),
            Array.Empty<IPatternAgent>(),
            Array.Empty<IStrategyAgent>(),
            new[] { failGuard },
            Array.Empty<IMetaLearningAgent>(),
            _logger);

        var result = await coordinator.RunRiskGauntletAsync(
            new Signal { Symbol = "BTCUSDT" },
            new TradeDecision { ShouldTrade = true },
            new List<Trade>());

        // Should pass through since the guard threw (not veto)
        result.Approved.Should().BeTrue();
    }

    [Fact]
    public async Task RiskGauntlet_Vetoes_WhenGuardBlocks()
    {
        var blockGuard = Substitute.For<IRiskGuardAgent>();
        blockGuard.GuardId.Returns("BlockGuard");
        blockGuard.Order.Returns(1);
        blockGuard.EvaluateAsync(Arg.Any<Signal>(), Arg.Any<TradeDecision>(),
                Arg.Any<IReadOnlyList<Trade>>(), Arg.Any<CancellationToken>())
            .Returns(RiskGuardResult.Block("BlockGuard", SignalReasonFlags.RiskVeto, "Test block"));

        var coordinator = new AgentCoordinator(
            Array.Empty<IFoundationAgent>(),
            Array.Empty<IPatternAgent>(),
            Array.Empty<IStrategyAgent>(),
            new[] { blockGuard },
            Array.Empty<IMetaLearningAgent>(),
            _logger);

        var result = await coordinator.RunRiskGauntletAsync(
            new Signal { Symbol = "BTCUSDT" },
            new TradeDecision { ShouldTrade = true },
            new List<Trade>());

        result.Approved.Should().BeFalse();
        result.VetoFlag.Should().HaveFlag(SignalReasonFlags.RiskVeto);
    }

    // ─── Meta-adaptation: failure → defaults ─────────────────────────────

    [Fact]
    public async Task MetaAdaptation_ReturnsDefault_WhenAllAgentsFail()
    {
        var failMeta = Substitute.For<IMetaLearningAgent>();
        failMeta.AgentId.Returns("FailMeta");
        failMeta.AdaptAsync(Arg.Any<IReadOnlyList<Trade>>(), Arg.Any<MarketRegime>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Meta crashed"));

        var coordinator = new AgentCoordinator(
            Array.Empty<IFoundationAgent>(),
            Array.Empty<IPatternAgent>(),
            Array.Empty<IStrategyAgent>(),
            Array.Empty<IRiskGuardAgent>(),
            new[] { failMeta },
            _logger);

        var result = await coordinator.RunMetaAdaptationAsync(
            new List<Trade>(), MarketRegime.Ranging);

        result.ConfidenceScalar.Should().Be(1f);
        result.StrategyWeights.Should().BeEmpty();
    }

    // ─── No agents registered ────────────────────────────────────────────

    [Fact]
    public async Task FullPipeline_HandlesEmptyAgentLists()
    {
        var coordinator = new AgentCoordinator(
            Array.Empty<IFoundationAgent>(),
            Array.Empty<IPatternAgent>(),
            Array.Empty<IStrategyAgent>(),
            Array.Empty<IRiskGuardAgent>(),
            Array.Empty<IMetaLearningAgent>(),
            _logger);

        var result = await coordinator.RunFullPipelineAsync(
            new Signal { Symbol = "BTCUSDT" },
            new List<MarketTick>(),
            new List<Candle>(),
            MarketRegime.Ranging,
            new List<Trade>(),
            new List<Trade>());

        result.Foundation.Should().Be(FoundationSnapshot.Empty);
        result.Patterns.Should().Be(PatternSnapshot.Empty);
        result.Votes.Should().BeEmpty();
        result.RiskGauntlet.Approved.Should().BeTrue();
    }
}
