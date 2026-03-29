using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.Strategy;

public sealed class AgentCoordinator : IAgentCoordinator
{
    private readonly IEnumerable<IFoundationAgent> _foundationAgents;
    private readonly IEnumerable<IPatternAgent> _patternAgents;
    private readonly IEnumerable<IStrategyAgent> _strategyAgents;
    private readonly IEnumerable<IRiskGuardAgent> _riskGuards;
    private readonly IEnumerable<IMetaLearningAgent> _metaAgents;
    private readonly ILogger<AgentCoordinator> _logger;

    public AgentCoordinator(
        IEnumerable<IFoundationAgent> foundationAgents,
        IEnumerable<IPatternAgent> patternAgents,
        IEnumerable<IStrategyAgent> strategyAgents,
        IEnumerable<IRiskGuardAgent> riskGuards,
        IEnumerable<IMetaLearningAgent> metaAgents,
        ILogger<AgentCoordinator> logger)
    {
        _foundationAgents = foundationAgents;
        _patternAgents = patternAgents;
        _strategyAgents = strategyAgents;
        _riskGuards = riskGuards;
        _metaAgents = metaAgents;
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
        var results = await Task.WhenAll(
            _foundationAgents.Select(a => RunSafe(a.AgentId, () => a.ComputeAsync(ticks, candles, ct))));

        return MergeFoundation(results);
    }

    public async Task<PatternSnapshot> BuildPatternAsync(
        Signal signal,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        var results = await Task.WhenAll(
            _patternAgents.Select(a => RunSafe(a.AgentId, () => a.AnalyzeAsync(signal, foundation, candles, ct))));

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
        var eligible = _strategyAgents
            .Where(a => a.CanHandle(signal, regime))
            .ToList();

        _logger.LogDebug("AgentCoordinator: {Count}/{Total} strategy agents eligible for {Symbol} [{Regime}]",
            eligible.Count, _strategyAgents.Count(), signal.Symbol, regime);

        var votes = await Task.WhenAll(
            eligible.Select(a => RunSafe(a.StrategyTag,
                () => a.EvaluateAsync(signal, patterns, foundation, candles, ct),
                StrategyVote.Abstain(a.StrategyTag))));

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
        foreach (var guard in _riskGuards.OrderBy(g => g.Order))
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
        var adaptations = await Task.WhenAll(
            _metaAgents.Select(a => RunSafe(a.AgentId,
                () => a.AdaptAsync(recentHistory, regime, ct),
                MetaAdaptation.Default)));

        return MergeAdaptations(adaptations);
    }

    private async Task<T> RunSafe<T>(string agentId, Func<Task<T>> action, T fallback = default!)
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
        var valid = snapshots.Where(s => s != null && s != FoundationSnapshot.Empty).ToList();
        if (valid.Count == 0) return FoundationSnapshot.Empty;

        var vwapSources = valid.Where(s => s.Vwap > 0).ToList();
        var volMap = valid.SelectMany(s => s.VolumeAtPrice)
            .GroupBy(kvp => kvp.Key)
            .ToDictionary(g => g.Key, g => g.Sum(kvp => kvp.Value));

        return new FoundationSnapshot
        {
            BidAskImbalance = valid.Average(s => s.BidAskImbalance),
            TickDirectionRatio = valid.Average(s => s.TickDirectionRatio),
            Vwap = vwapSources.Count > 0 ? vwapSources.Average(s => s.Vwap) : 0m,
            VwapStdDev = vwapSources.Count > 0 ? vwapSources.Average(s => s.VwapStdDev) : 0m,
            SpreadBps = valid.Average(s => s.SpreadBps),
            WallLevel = valid.Select(s => s.WallLevel).FirstOrDefault(w => w > 0),
            VolumeAtPrice = volMap
        };
    }

    private static PatternSnapshot MergePatterns(PatternSnapshot[] snapshots)
    {
        var valid = snapshots.Where(s => s != null && s != PatternSnapshot.Empty).ToList();
        if (valid.Count == 0) return PatternSnapshot.Empty;

        var tags = valid.SelectMany(s => s.PatternTags).Distinct().ToList();
        var ichimoku = valid.Select(s => s.IchimokuState)
            .FirstOrDefault(s => s != IchimokuState.Unknown);

        // Use NaN as "no data" sentinel — 0.0 is a valid value (price at midline / VWAP).
        // Downstream agents must check double.IsNaN() before using these fields.
        var bollingerSources = valid.Where(s => !double.IsNaN(s.BollingerPercentB) && s.BollingerPercentB != 0).ToList();
        var vwapSources = valid.Where(s => !double.IsNaN(s.VwapDeviationSigmas) && s.VwapDeviationSigmas != 0).ToList();

        return new PatternSnapshot
        {
            BollingerPercentB = bollingerSources.Count > 0 ? bollingerSources.Average(s => s.BollingerPercentB) : double.NaN,
            BollingerSqueeze = valid.Any(s => s.BollingerSqueeze),
            FibNearestLevel = valid.Select(s => s.FibNearestLevel).FirstOrDefault(f => f > 0),
            FibRetracementDepth = valid.Select(s => s.FibRetracementDepth).FirstOrDefault(f => f != 0),
            IchimokuState = ichimoku,
            VwapDeviationSigmas = vwapSources.Count > 0 ? vwapSources.Average(s => s.VwapDeviationSigmas) : double.NaN,
            PatternTags = tags
        };
    }

    private static MetaAdaptation MergeAdaptations(MetaAdaptation[] adaptations)
    {
        var sums = new Dictionary<string, float>();
        var counts = new Dictionary<string, int>();
        foreach (var adaptation in adaptations)
        {
            foreach (var (tag, weight) in adaptation.StrategyWeights)
            {
                sums[tag] = sums.TryGetValue(tag, out var existing) ? existing + weight : weight;
                counts[tag] = counts.TryGetValue(tag, out var c) ? c + 1 : 1;
            }
        }
        var merged = sums.ToDictionary(kvp => kvp.Key, kvp => kvp.Value / counts[kvp.Key]);

        var avgBias = adaptations.Length > 0 ? adaptations.Average(a => a.RecommendedRegimeBias) : 1f;
        var avgScalar = adaptations.Length > 0 ? adaptations.Average(a => a.ConfidenceScalar) : 1f;

        return new MetaAdaptation
        {
            StrategyWeights = merged,
            RecommendedRegimeBias = avgBias,
            ConfidenceScalar = avgScalar
        };
    }
}
