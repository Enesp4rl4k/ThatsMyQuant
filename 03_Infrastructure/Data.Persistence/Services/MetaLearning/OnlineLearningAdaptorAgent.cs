using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ThatsMyQuant.Infrastructure.Services.MetaLearning;

public sealed class OnlineLearningAdaptorAgent : IMetaLearningAgent
{
    private readonly ILogger<OnlineLearningAdaptorAgent> _logger;
    private readonly Dictionary<string, ExponentialWeightedStat> _strategyStats = new();
    private readonly float _decayAlpha;
    private readonly float _baseWeight;
    private readonly int _minHistoryForAdaptation;

    public string AgentId => "Meta.OnlineLearning";

    public OnlineLearningAdaptorAgent(ILogger<OnlineLearningAdaptorAgent> logger, IOptions<MetaLearningAgentOptions>? options = null)
    {
        _logger = logger;
        var opts = options?.Value ?? new MetaLearningAgentOptions();
        _decayAlpha = opts.DecayAlpha;
        _baseWeight = opts.BaseWeight;
        _minHistoryForAdaptation = opts.MinHistoryForAdaptation;
    }

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
                stat = new ExponentialWeightedStat(_decayAlpha);
                _strategyStats[trade.StrategyTag] = stat;
            }

            var outcome = trade.PnlPercent > 0 ? 1f : 0f;
            stat.Update(outcome);
        }

        if (_strategyStats.Count < 1 || recentHistory.Count < _minHistoryForAdaptation)
            return Task.FromResult(MetaAdaptation.Default);

        var weights = _strategyStats
            .ToDictionary(
                kvp => kvp.Key,
                kvp => _baseWeight + kvp.Value.EwmaWinRate - 0.5f);

        return Task.FromResult(new MetaAdaptation
        {
            StrategyWeights = weights,
            ConfidenceScalar = 1.0f
        });
    }

    private sealed class ExponentialWeightedStat
    {
        private readonly float _alpha;
        public float EwmaWinRate { get; private set; } = 0.5f;

        public ExponentialWeightedStat(float alpha = 0.05f) => _alpha = alpha;

        public void Update(float outcome)
        {
            EwmaWinRate = (1 - _alpha) * EwmaWinRate + _alpha * outcome;
        }
    }
}
