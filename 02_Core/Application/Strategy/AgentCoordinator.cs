using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.Strategy;

public class AgentCoordinator : IAgentCoordinator
{
    private readonly IFoundationAgent[] _foundationAgents;
    private readonly IPatternAgent[] _patternAgents;
    private readonly IStrategyAgent[] _strategyAgents;
    private readonly IRiskGuardAgent[] _riskGuards;
    private readonly IMetaLearningAgent[] _metaAgents;
    private readonly ILogger<AgentCoordinator> _logger;

    public AgentCoordinator(
        IEnumerable<IFoundationAgent> foundationAgents,
        IEnumerable<IPatternAgent> patternAgents,
        IEnumerable<IStrategyAgent> strategyAgents,
        IEnumerable<IRiskGuardAgent> riskGuards,
        IEnumerable<IMetaLearningAgent> metaAgents,
        ILogger<AgentCoordinator> logger)
    {
        _foundationAgents = foundationAgents.ToArray();
        _patternAgents = patternAgents.ToArray();
        _strategyAgents = strategyAgents.ToArray();
        _riskGuards = riskGuards.OrderBy(g => g.Order).ToArray();
        _metaAgents = metaAgents.ToArray();
        _logger = logger;
    }

    public async Task ResetAllAgentsAsync(string symbol, CancellationToken ct = default)
    {
        var allAgents = _foundationAgents.Cast<object>()
            .Concat(_patternAgents)
            .Concat(_strategyAgents)
            .Concat(_riskGuards)
            .Concat(_metaAgents)
            .OfType<IFocusedAgent>();

        foreach (var agent in allAgents)
        {
            await agent.ResetAsync(symbol, ct);
        }
        _logger.LogInformation("All supported agents reset for {Symbol}", symbol);
    }

    public async Task<FoundationSnapshot> BuildFoundationAsync(
        IReadOnlyList<MarketTick> ticks,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        var tasks = new Task<FoundationSnapshot>[_foundationAgents.Length];
        for (var i = 0; i < _foundationAgents.Length; i++)
        {
            var agent = _foundationAgents[i];
            tasks[i] = RunSafe(
                agent.AgentId,
                () => agent.ComputeAsync(ticks, candles, ct),
                FoundationSnapshot.Empty);
        }

        var results = await Task.WhenAll(tasks);

        return MergeFoundation(results);
    }

    public async Task<PatternSnapshot> BuildPatternAsync(
        Signal signal,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        var tasks = new Task<PatternSnapshot>[_patternAgents.Length];
        for (var i = 0; i < _patternAgents.Length; i++)
        {
            var agent = _patternAgents[i];
            tasks[i] = RunSafe(
                agent.AgentId,
                () => agent.AnalyzeAsync(signal, foundation, candles, ct),
                PatternSnapshot.Empty);
        }

        var results = await Task.WhenAll(tasks);

        return MergePatterns(results);
    }

    public async Task<IReadOnlyList<StrategyVote>> CollectStrategyVotesAsync(
        Signal signal,
        PatternSnapshot patterns,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        MarketRegime regime,
        CancellationToken ct = default)
    {
        var eligibleCount = 0;
        for (var i = 0; i < _strategyAgents.Length; i++)
        {
            if (_strategyAgents[i].CanHandle(signal, regime))
            {
                eligibleCount++;
            }
        }

        _logger.LogDebug("AgentCoordinator: {Count}/{Total} strategy agents eligible for {Symbol} [{Regime}]",
            eligibleCount, _strategyAgents.Length, signal.Symbol, regime);

        if (eligibleCount == 0)
        {
            return [];
        }

        var tasks = new Task<StrategyVote>[eligibleCount];
        var taskIndex = 0;
        for (var i = 0; i < _strategyAgents.Length; i++)
        {
            var agent = _strategyAgents[i];
            if (!agent.CanHandle(signal, regime))
            {
                continue;
            }

            tasks[taskIndex++] = RunSafe(
                agent.StrategyTag,
                () => agent.EvaluateAsync(signal, patterns, foundation, candles, ct),
                StrategyVote.Abstain(agent.StrategyTag));
        }

        var votes = await Task.WhenAll(tasks);

        return votes;
    }

    public async Task<FiveTierSnapshot> RunFullPipelineAsync(
        Signal signal,
        IReadOnlyList<MarketTick> ticks,
        IReadOnlyList<Candle> candles,
        MarketRegime regime,
        IReadOnlyList<Trade> openTrades,
        IReadOnlyList<Trade> recentHistory,
        CancellationToken ct = default)
    {
        var foundation = await BuildFoundationAsync(ticks, candles, ct);
        var patterns = await BuildPatternAsync(signal, foundation, candles, ct);
        var votes = await CollectStrategyVotesAsync(signal, patterns, foundation, candles, regime, ct);
        var meta = await RunMetaAdaptationAsync(recentHistory, regime, ct);

        // For telemetry purposes, we run a "shadow" risk gauntlet on a hypothetical decision
        var shadowDecision = new TradeDecision { ShouldTrade = true, Confidence = 50 };
        var riskResult = await RunRiskGauntletAsync(signal, shadowDecision, openTrades, ct);

        return new FiveTierSnapshot
        {
            Foundation = foundation,
            Patterns = patterns,
            Votes = votes,
            RiskGauntlet = riskResult,
            Meta = meta,
            Timestamp = DateTime.UtcNow
        };
    }

    public async Task<RiskGuardResult> RunRiskGauntletAsync(
        Signal signal,
        TradeDecision decision,
        IReadOnlyList<Trade> openTrades,
        CancellationToken ct = default)
    {
        foreach (var guard in _riskGuards)
        {
            RiskGuardResult result;
            try
            {
                result = await guard.EvaluateAsync(signal, decision, openTrades, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RiskGuard [{GuardId}] threw — allowing pass-through", guard.GuardId);
                continue;
            }

            if (!result.Approved)
            {
                _logger.LogWarning("RiskGuard [{GuardId}] vetoed {Symbol}: {Reason}",
                    guard.GuardId, signal.Symbol, result.Reason);
                return result;
            }

            if (result.SizeMultiplier < 1m)
                _logger.LogInformation("RiskGuard [{GuardId}] scaled size to {Mult:P0} for {Symbol}",
                    guard.GuardId, result.SizeMultiplier, signal.Symbol);
        }

        return RiskGuardResult.Pass("Gauntlet.Passed");
    }

    public async Task<MetaAdaptation> RunMetaAdaptationAsync(
        IReadOnlyList<Trade> recentHistory,
        MarketRegime regime,
        CancellationToken ct = default)
    {
        var tasks = new Task<MetaAdaptation>[_metaAgents.Length];
        for (var i = 0; i < _metaAgents.Length; i++)
        {
            var agent = _metaAgents[i];
            tasks[i] = RunSafe(
                agent.AgentId,
                () => agent.AdaptAsync(recentHistory, regime, ct),
                MetaAdaptation.Default);
        }

        var adaptations = await Task.WhenAll(tasks);

        return MergeAdaptations(adaptations);
    }

    private async Task<T> RunSafe<T>(string agentId, Func<Task<T>> action, T fallback)
        where T : notnull
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent [{AgentId}] failed — using fallback result", agentId);
            return fallback;
        }
    }

    private static FoundationSnapshot MergeFoundation(FoundationSnapshot[] snapshots)
    {
        var validCount = 0;
        var vwapCount = 0;
        double bidAskImbalanceSum = 0d;
        double tickDirectionRatioSum = 0d;
        double spreadBpsSum = 0d;
        decimal vwapSum = 0m;
        decimal vwapStdDevSum = 0m;
        decimal wallLevel = 0m;
        var volumeAtPrice = new Dictionary<decimal, decimal>();

        for (var i = 0; i < snapshots.Length; i++)
        {
            var snapshot = snapshots[i];
            if (snapshot == FoundationSnapshot.Empty)
            {
                continue;
            }

            validCount++;
            bidAskImbalanceSum += snapshot.BidAskImbalance;
            tickDirectionRatioSum += snapshot.TickDirectionRatio;
            spreadBpsSum += snapshot.SpreadBps;

            if (snapshot.Vwap > 0)
            {
                vwapCount++;
                vwapSum += snapshot.Vwap;
                vwapStdDevSum += snapshot.VwapStdDev;
            }

            if (wallLevel <= 0 && snapshot.WallLevel > 0)
            {
                wallLevel = snapshot.WallLevel;
            }

            foreach (var pair in snapshot.VolumeAtPrice)
            {
                volumeAtPrice[pair.Key] = volumeAtPrice.TryGetValue(pair.Key, out var existingVolume)
                    ? existingVolume + pair.Value
                    : pair.Value;
            }
        }

        if (validCount == 0)
        {
            return FoundationSnapshot.Empty;
        }

        return new FoundationSnapshot
        {
            BidAskImbalance = bidAskImbalanceSum / validCount,
            TickDirectionRatio = tickDirectionRatioSum / validCount,
            Vwap = vwapCount > 0 ? vwapSum / vwapCount : 0m,
            VwapStdDev = vwapCount > 0 ? vwapStdDevSum / vwapCount : 0m,
            SpreadBps = spreadBpsSum / validCount,
            WallLevel = wallLevel,
            VolumeAtPrice = volumeAtPrice
        };
    }

    private static PatternSnapshot MergePatterns(PatternSnapshot[] snapshots)
    {
        var validCount = 0;
        var bollingerPercentBCount = 0;
        var vwapDeviationCount = 0;
        double bollingerPercentBSum = 0d;
        double vwapDeviationSum = 0d;
        bool bollingerSqueeze = false;
        decimal fibNearestLevel = 0m;
        double fibRetracementDepth = 0d;
        var ichimoku = IchimokuState.Unknown;
        var tags = new HashSet<string>();

        for (var i = 0; i < snapshots.Length; i++)
        {
            var snapshot = snapshots[i];
            if (snapshot == PatternSnapshot.Empty)
            {
                continue;
            }

            validCount++;

            if (snapshot.BollingerPercentB != 0)
            {
                bollingerPercentBCount++;
                bollingerPercentBSum += snapshot.BollingerPercentB;
            }

            if (snapshot.BollingerSqueeze)
            {
                bollingerSqueeze = true;
            }

            if (fibNearestLevel <= 0 && snapshot.FibNearestLevel > 0)
            {
                fibNearestLevel = snapshot.FibNearestLevel;
            }

            if (fibRetracementDepth == 0d && snapshot.FibRetracementDepth != 0d)
            {
                fibRetracementDepth = snapshot.FibRetracementDepth;
            }

            if (ichimoku == IchimokuState.Unknown && snapshot.IchimokuState != IchimokuState.Unknown)
            {
                ichimoku = snapshot.IchimokuState;
            }

            if (snapshot.VwapDeviationSigmas != 0)
            {
                vwapDeviationCount++;
                vwapDeviationSum += snapshot.VwapDeviationSigmas;
            }

            foreach (var tag in snapshot.PatternTags)
            {
                tags.Add(tag);
            }
        }

        if (validCount == 0)
        {
            return PatternSnapshot.Empty;
        }

        return new PatternSnapshot
        {
            BollingerPercentB = bollingerPercentBCount > 0 ? bollingerPercentBSum / bollingerPercentBCount : 0d,
            BollingerSqueeze = bollingerSqueeze,
            FibNearestLevel = fibNearestLevel,
            FibRetracementDepth = fibRetracementDepth,
            IchimokuState = ichimoku,
            VwapDeviationSigmas = vwapDeviationCount > 0 ? vwapDeviationSum / vwapDeviationCount : 0d,
            PatternTags = [.. tags]
        };
    }

    private static MetaAdaptation MergeAdaptations(MetaAdaptation[] adaptations)
    {
        var merged = new Dictionary<string, float>();
        float biasSum = 0f, scalarSum = 0f;

        foreach (var adaptation in adaptations)
        {
            biasSum += adaptation.RecommendedRegimeBias;
            scalarSum += adaptation.ConfidenceScalar;

            foreach (var (tag, weight) in adaptation.StrategyWeights)
            {
                merged[tag] = merged.TryGetValue(tag, out var existing)
                    ? (existing + weight) / 2f
                    : weight;
            }
        }

        var avgBias = adaptations.Length > 0 ? biasSum / adaptations.Length : 1f;
        var avgScalar = adaptations.Length > 0 ? scalarSum / adaptations.Length : 1f;

        return new MetaAdaptation
        {
            StrategyWeights = merged,
            RecommendedRegimeBias = avgBias,
            ConfidenceScalar = avgScalar
        };
    }
}
