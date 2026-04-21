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
                _logger.LogDebug("RiskGuard [{GuardId}] vetoed {Symbol}: {Reason}",
                    guard.GuardId, signal.Symbol, result.Reason);
                return result;
            }

            if (result.SizeMultiplier < 1m)
                _logger.LogDebug("RiskGuard [{GuardId}] scaled size to {Mult:P0} for {Symbol}",
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
        var valid = snapshots.Where(s => s != FoundationSnapshot.Empty).ToList();
        if (valid.Count == 0) return FoundationSnapshot.Empty;

        var vwapValid = valid.Where(s => s.Vwap > 0).ToList();
        var volumeAtPrice = new Dictionary<decimal, decimal>();

        foreach (var pair in valid.SelectMany(s => s.VolumeAtPrice))
        {
            volumeAtPrice[pair.Key] = volumeAtPrice.GetValueOrDefault(pair.Key) + pair.Value;
        }

        return new FoundationSnapshot
        {
            BidAskImbalance = valid.Average(s => s.BidAskImbalance),
            TickDirectionRatio = valid.Average(s => s.TickDirectionRatio),
            Vwap = vwapValid.Count > 0 ? vwapValid.Average(s => s.Vwap) : 0m,
            VwapStdDev = vwapValid.Count > 0 ? vwapValid.Average(s => s.VwapStdDev) : 0m,
            SpreadBps = valid.Average(s => s.SpreadBps),
            WallLevel = valid.FirstOrDefault(s => s.WallLevel > 0)?.WallLevel ?? 0m,
            VolumeAtPrice = volumeAtPrice
        };
    }

    private static PatternSnapshot MergePatterns(PatternSnapshot[] snapshots)
    {
        var valid = snapshots.Where(s => s != PatternSnapshot.Empty).ToList();
        if (valid.Count == 0) return PatternSnapshot.Empty;

        var bBands = valid.Where(s => Math.Abs(s.BollingerPercentB) > 0.000001d).ToList();
        var vWaps = valid.Where(s => Math.Abs(s.VwapDeviationSigmas) > 0.000001d).ToList();
        
        var tags = valid.SelectMany(s => s.PatternTags).Distinct().ToList();

        return new PatternSnapshot
        {
            BollingerPercentB = bBands.Count > 0 ? bBands.Average(s => s.BollingerPercentB) : 0d,
            BollingerSqueeze = valid.Any(s => s.BollingerSqueeze),
            FibNearestLevel = valid.FirstOrDefault(s => s.FibNearestLevel > 0)?.FibNearestLevel ?? 0m,
            FibRetracementDepth = valid.FirstOrDefault(s => Math.Abs(s.FibRetracementDepth) > 0.000001d)?.FibRetracementDepth ?? 0d,
            IchimokuState = valid.FirstOrDefault(s => s.IchimokuState != IchimokuState.Unknown)?.IchimokuState ?? IchimokuState.Unknown,
            VwapDeviationSigmas = vWaps.Count > 0 ? vWaps.Average(s => s.VwapDeviationSigmas) : 0d,
            PatternTags = tags.ToArray()
        };
    }

    private static MetaAdaptation MergeAdaptations(MetaAdaptation[] adaptations)
    {
        if (adaptations.Length == 0) return MetaAdaptation.Default;

        var mergedWeights = adaptations
            .SelectMany(a => a.StrategyWeights)
            .GroupBy(kvp => kvp.Key)
            .ToDictionary(g => g.Key, g => g.Average(kvp => kvp.Value));

        return new MetaAdaptation
        {
            StrategyWeights = mergedWeights,
            RecommendedRegimeBias = adaptations.Average(a => a.RecommendedRegimeBias),
            ConfidenceScalar = adaptations.Average(a => a.ConfidenceScalar)
        };
    }
}
