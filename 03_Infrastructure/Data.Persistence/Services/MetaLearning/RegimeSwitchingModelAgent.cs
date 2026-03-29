using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.MetaLearning;

public sealed class RegimeSwitchingModelAgent : IMetaLearningAgent
{
    private readonly ILogger<RegimeSwitchingModelAgent> _logger;

    private static readonly IReadOnlyDictionary<MarketRegime, IReadOnlyDictionary<string, float>> RegimeWeightTable =
        NormalizeWeightTable(new Dictionary<MarketRegime, Dictionary<string, float>>
        {
            [MarketRegime.Trending] = new()
            {
                ["Momentum"] = 1.4f, ["Breakout"] = 1.3f, ["StatArb"] = 0.5f,
                ["Scalping"] = 0.7f, ["HFT-PriceAction"] = 0.9f
            },
            [MarketRegime.Breakout] = new()
            {
                ["Momentum"] = 1.2f, ["Breakout"] = 1.6f, ["StatArb"] = 0.3f,
                ["Scalping"] = 0.8f, ["HFT-PriceAction"] = 1.1f
            },
            [MarketRegime.Ranging] = new()
            {
                ["Momentum"] = 0.6f, ["Breakout"] = 0.4f, ["StatArb"] = 1.4f,
                ["Scalping"] = 1.3f, ["HFT-PriceAction"] = 1.0f
            },
            [MarketRegime.MeanReverting] = new()
            {
                ["Momentum"] = 0.4f, ["Breakout"] = 0.3f, ["StatArb"] = 1.7f,
                ["Scalping"] = 1.1f, ["HFT-PriceAction"] = 0.8f
            },
            [MarketRegime.Volatile] = new()
            {
                ["Momentum"] = 0.5f, ["Breakout"] = 0.7f, ["StatArb"] = 0.6f,
                ["Scalping"] = 0.4f, ["HFT-PriceAction"] = 0.5f
            },
        });

    /// <summary>
    /// Normalizes each regime's weight dictionary so all regimes sum to the same total (number of strategies).
    /// This prevents implicit regime bias from unequal raw weight sums.
    /// </summary>
    private static IReadOnlyDictionary<MarketRegime, IReadOnlyDictionary<string, float>> NormalizeWeightTable(
        Dictionary<MarketRegime, Dictionary<string, float>> raw)
    {
        var result = new Dictionary<MarketRegime, IReadOnlyDictionary<string, float>>();
        foreach (var (regime, weights) in raw)
        {
            float sum = weights.Values.Sum();
            float targetSum = weights.Count; // normalize so average weight = 1.0
            float scale = sum > 0 ? targetSum / sum : 1f;
            result[regime] = weights.ToDictionary(kvp => kvp.Key, kvp => kvp.Value * scale);
        }
        return result;
    }

    public string AgentId => "Meta.RegimeSwitching";

    public RegimeSwitchingModelAgent(ILogger<RegimeSwitchingModelAgent> logger)
        => _logger = logger;

    public Task<MetaAdaptation> AdaptAsync(
        IReadOnlyList<Trade> recentHistory,
        MarketRegime regime,
        CancellationToken ct = default)
    {
        var weights = RegimeWeightTable.TryGetValue(regime, out var regimeWeights)
            ? regimeWeights
            : (IReadOnlyDictionary<string, float>)new Dictionary<string, float>();

        var regimeBias = regime == MarketRegime.Volatile ? 0.7f : 1.0f;

        _logger.LogDebug("RegimeSwitching [{Regime}]: applied {Count} strategy weight overrides",
            regime, weights.Count);

        return Task.FromResult(new MetaAdaptation
        {
            StrategyWeights = weights,
            RecommendedRegimeBias = regimeBias,
            ConfidenceScalar = regimeBias
        });
    }
}
