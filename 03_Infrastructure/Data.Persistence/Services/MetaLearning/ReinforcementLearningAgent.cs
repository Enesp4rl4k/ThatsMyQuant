using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ThatsMyQuant.Infrastructure.Services.MetaLearning;

public sealed class ReinforcementLearningAgent : IMetaLearningAgent
{
    private readonly ILogger<ReinforcementLearningAgent> _logger;
    private readonly Dictionary<string, ArmState> _arms = new();
    private readonly double _explorationEpsilon;
    private readonly int _warmupTrades;

    public string AgentId => "Meta.RLBandit";

    public ReinforcementLearningAgent(ILogger<ReinforcementLearningAgent> logger, IOptions<MetaLearningAgentOptions>? options = null)
    {
        _logger = logger;
        var opts = options?.Value ?? new MetaLearningAgentOptions();
        _explorationEpsilon = opts.ExplorationEpsilon;
        _warmupTrades = opts.WarmupTrades;
    }

    public Task<MetaAdaptation> AdaptAsync(
        IReadOnlyList<Trade> recentHistory,
        MarketRegime regime,
        CancellationToken ct = default)
    {
        foreach (var trade in recentHistory.TakeLast(30))
        {
            if (string.IsNullOrEmpty(trade.StrategyTag)) continue;

            if (!_arms.TryGetValue(trade.StrategyTag, out var arm))
            {
                arm = new ArmState();
                _arms[trade.StrategyTag] = arm;
            }

            if (arm.Pulls == 0) arm.CurrentValue = 0.5; // Initial guess

            var reward = (double)trade.PnlPercent; // Realised PnL as reward
            arm.Pulls++;
            // Incremental mean update: Q(n+1) = Q(n) + 1/n * [R - Q(n)]
            arm.CurrentValue += (1.0 / arm.Pulls) * (reward - arm.CurrentValue);
        }

        if (_arms.Count == 0)
            return Task.FromResult(MetaAdaptation.Default);

        var weights = new Dictionary<string, float>();
        var rng = Random.Shared;

        foreach (var (tag, arm) in _arms)
        {
            if (arm.Pulls < _warmupTrades)
            {
                weights[tag] = 1.0f;
                continue;
            }

            var explore = rng.NextDouble() < _explorationEpsilon;
            var score = explore ? rng.NextDouble() : arm.WinRate;
            weights[tag] = (float)(0.5 + score);
        }

        var bestArm = _arms.OrderByDescending(a => a.Value.WinRate).FirstOrDefault();
        _logger.LogDebug("RL Bandit best arm: {Tag} (WinRate={WinRate:P1}, Pulls={Pulls})",
            bestArm.Key, bestArm.Value?.WinRate, bestArm.Value?.Pulls);

        return Task.FromResult(new MetaAdaptation
        {
            StrategyWeights = weights,
            ConfidenceScalar = weights.Count > 0 ? weights.Values.Average() : 1.0f
        });
    }

    private sealed class ArmState
    {
        public int Pulls { get; set; }
        public double CurrentValue { get; set; }
        public double WinRate => CurrentValue; // Interpreted as average reward
    }
}
