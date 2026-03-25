using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.MetaLearning;

public sealed class OnlineLearningAdaptorAgent : IMetaLearningAgent
{
    private readonly ILogger<OnlineLearningAdaptorAgent> _logger;
    private readonly Dictionary<string, ExponentialWeightedStat> _strategyStats = new();
    private const float DecayAlpha = 0.05f;
    private const float BaseWeight = 1.0f;
    private const int MinHistoryForAdaptation = 5;

    public string AgentId => "Meta.OnlineLearning";

    public OnlineLearningAdaptorAgent(ILogger<OnlineLearningAdaptorAgent> logger)
        => _logger = logger;

    public Task<MetaAdaptation> AdaptAsync(
        IReadOnlyList<Trade> recentHistory,
        MarketRegime regime,
        CancellationToken ct = default)
    {
        foreach (var trade in recentHistory.TakeLast(20))
        {
            if (string.IsNullOrEmpty(trade.StrategyTag)) continue;

            if (!_strategyStats.TryGetValue(trade.StrategyTag, out var stat))
            {
                stat = new ExponentialWeightedStat();
                _strategyStats[trade.StrategyTag] = stat;
            }

            var outcome = trade.PnlPercent > 0 ? 1f : 0f;
            stat.Update(outcome);
        }

        if (_strategyStats.Count < 1 || recentHistory.Count < MinHistoryForAdaptation)
            return Task.FromResult(MetaAdaptation.Default);

        var weights = _strategyStats
            .ToDictionary(
                kvp => kvp.Key,
                kvp => BaseWeight + kvp.Value.EwmaWinRate - 0.5f);

        return Task.FromResult(new MetaAdaptation
        {
            StrategyWeights = weights,
            ConfidenceScalar = 1.0f
        });
    }

    private sealed class ExponentialWeightedStat
    {
        public float EwmaWinRate { get; private set; } = 0.5f;

        public void Update(float outcome)
        {
            EwmaWinRate = (1 - DecayAlpha) * EwmaWinRate + DecayAlpha * outcome;
        }
    }
}
