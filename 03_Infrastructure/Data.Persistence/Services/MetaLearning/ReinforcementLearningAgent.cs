using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Infrastructure.Services.MetaLearning;

/// <summary>
/// Multi-armed bandit agent that dynamically adjusts strategy weights based on
/// actual trade outcomes. Replaces arbitrary hardcoded SwarmWeight values with
/// data-driven allocation.
///
/// Algorithm: UCB1 (Upper Confidence Bound) with risk-adjusted rewards.
///   - Exploration bonus: c × √(ln(N) / n_i) encourages trying under-sampled strategies
///   - Reward signal: risk-adjusted PnL (Sharpe-like) — not raw PnL%
///   - Weight output: softmax over UCB scores → proper probability distribution
///   - Warmup: strategies with < WarmupTrades use a neutral prior (1.0)
///
/// Persistence: arm state (pulls, mean reward, reward variance) is saved to DB
/// and restored on startup so the bandit retains learning across restarts.
/// </summary>
public sealed class ReinforcementLearningAgent : IMetaLearningAgent
{
    private readonly ILogger<ReinforcementLearningAgent> _logger;
    private readonly IDbContextFactory<QuantDbContext> _dbFactory;
    private readonly ConcurrentDictionary<string, ArmState> _arms = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _stateLoaded;

    // UCB1 exploration coefficient — √2 is theoretically optimal, 1.0 is more conservative
    internal const double ExplorationCoefficient = 1.0;

    // Minimum trades before UCB kicks in; below this, weight = 1.0 (neutral prior)
    internal const int WarmupTrades = 10;

    // Softmax temperature — lower = more aggressive differentiation between strategies
    internal const double SoftmaxTemperature = 0.5;

    public string AgentId => "Meta.RLBandit";

    public ReinforcementLearningAgent(
        ILogger<ReinforcementLearningAgent> logger,
        IDbContextFactory<QuantDbContext> dbFactory)
    {
        _logger = logger;
        _dbFactory = dbFactory;
    }

    public async Task<MetaAdaptation> AdaptAsync(
        IReadOnlyList<Trade> recentHistory,
        MarketRegime regime,
        CancellationToken ct = default)
    {
        await EnsureStateLoadedAsync(ct);

        // ── Update arm statistics from recent trades ──
        foreach (var trade in recentHistory.TakeLast(30))
        {
            if (string.IsNullOrEmpty(trade.StrategyTag) || !trade.IsClosed) continue;

            var arm = _arms.GetOrAdd(trade.StrategyTag, _ => new ArmState());
            var reward = (double)trade.PnlPercent;
            arm.Update(reward);
        }

        if (_arms.Count == 0)
            return MetaAdaptation.Default;

        // ── Compute UCB1 scores and convert to weights via softmax ──
        int totalPulls = _arms.Values.Sum(a => a.Pulls);
        var weights = ComputeUcbWeights(_arms, totalPulls);

        var bestArm = _arms.MaxBy(a => a.Value.RiskAdjustedValue);
        _logger.LogDebug(
            "[{AgentId}] Best arm: {Tag} (AvgReward={Avg:F4}, Sharpe={Sharpe:F2}, Pulls={Pulls}, Weight={Weight:F3})",
            AgentId, bestArm.Key, bestArm.Value.MeanReward, bestArm.Value.RiskAdjustedValue,
            bestArm.Value.Pulls, weights.GetValueOrDefault(bestArm.Key));

        await SaveStateAsync(ct);

        return new MetaAdaptation
        {
            StrategyWeights = weights,
            ConfidenceScalar = ComputeConfidenceScalar(weights)
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  UCB1 + Softmax Weight Computation
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes strategy weights using UCB1 scores normalized via softmax.
    /// Strategies in warmup get weight 1.0 (neutral). Mature strategies get
    /// weights proportional to exp(UCB_score / temperature).
    /// </summary>
    internal static Dictionary<string, float> ComputeUcbWeights(
        IDictionary<string, ArmState> arms, int totalPulls)
    {
        var weights = new Dictionary<string, float>(arms.Count);
        var matureArms = new List<(string Tag, double UcbScore)>();

        foreach (var (tag, arm) in arms)
        {
            if (arm.Pulls < WarmupTrades)
            {
                weights[tag] = 1.0f;
                continue;
            }

            double ucbScore = ComputeUcbScore(arm, totalPulls);
            matureArms.Add((tag, ucbScore));
        }

        if (matureArms.Count > 0)
        {
            var softmaxWeights = Softmax(matureArms.Select(a => a.UcbScore).ToArray(), SoftmaxTemperature);
            for (int i = 0; i < matureArms.Count; i++)
            {
                // Scale softmax output (0-1 range, sums to 1) to usable weight range.
                // Multiply by arm count so average weight ≈ 1.0, preserving relative ordering.
                weights[matureArms[i].Tag] = (float)(softmaxWeights[i] * matureArms.Count);
            }
        }

        return weights;
    }

    /// <summary>
    /// UCB1 score: risk-adjusted mean reward + exploration bonus.
    /// Uses Sharpe-like reward (mean / stddev) so a strategy that wins small but
    /// consistently beats one that wins big but erratically.
    /// </summary>
    internal static double ComputeUcbScore(ArmState arm, int totalPulls)
    {
        double exploitation = arm.RiskAdjustedValue;
        double exploration = totalPulls > 0 && arm.Pulls > 0
            ? ExplorationCoefficient * Math.Sqrt(Math.Log(totalPulls) / arm.Pulls)
            : 0;

        return exploitation + exploration;
    }

    /// <summary>
    /// Softmax: converts raw scores to a probability distribution.
    /// Temperature controls differentiation: lower = winner-take-all, higher = uniform.
    /// </summary>
    internal static double[] Softmax(double[] scores, double temperature)
    {
        if (scores.Length == 0) return [];
        if (scores.Length == 1) return [1.0];

        // Subtract max for numerical stability
        double max = scores.Max();
        var exps = scores.Select(s => Math.Exp((s - max) / temperature)).ToArray();
        double sum = exps.Sum();

        return sum > 0
            ? exps.Select(e => e / sum).ToArray()
            : Enumerable.Repeat(1.0 / scores.Length, scores.Length).ToArray();
    }

    /// <summary>
    /// Confidence scalar: average weight when all strategies have data,
    /// biased toward 1.0 to avoid extreme swings in overall confidence.
    /// </summary>
    private static float ComputeConfidenceScalar(Dictionary<string, float> weights)
    {
        if (weights.Count == 0) return 1.0f;
        float avg = weights.Values.Average();
        // Blend toward 1.0: 70% data-driven, 30% anchor to avoid wild swings
        return 0.7f * avg + 0.3f;
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    private async Task EnsureStateLoadedAsync(CancellationToken ct)
    {
        if (_stateLoaded) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_stateLoaded) return;
            await LoadStateAsync(ct);
            _stateLoaded = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task LoadStateAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var record = await db.MetaLearningStates.FindAsync(new object[] { AgentId }, ct);
            if (record == null) return;

            var saved = JsonSerializer.Deserialize<Dictionary<string, ArmSnapshot>>(record.StateJson);
            if (saved == null) return;

            foreach (var (tag, snap) in saved)
                _arms[tag] = new ArmState
                {
                    Pulls = snap.Pulls,
                    MeanReward = snap.MeanReward,
                    VarianceAccumulator = snap.VarianceAccumulator
                };

            _logger.LogInformation("[{AgentId}] Loaded {Count} bandit arms from DB (updated {At:u})",
                AgentId, saved.Count, record.UpdatedAt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{AgentId}] Could not load state from DB — starting fresh", AgentId);
        }
    }

    private async Task SaveStateAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = _arms.ToDictionary(
                kvp => kvp.Key,
                kvp => new ArmSnapshot(kvp.Value.Pulls, kvp.Value.MeanReward, kvp.Value.VarianceAccumulator));

            var json = JsonSerializer.Serialize(snapshot);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var record = await db.MetaLearningStates.FindAsync(new object[] { AgentId }, ct);
            if (record == null)
            {
                db.MetaLearningStates.Add(new MetaLearningStateRecord
                {
                    AgentId = AgentId,
                    StateJson = json,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                record.StateJson = json;
                record.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{AgentId}] Could not save state to DB — in-memory state intact", AgentId);
        }
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    /// <summary>
    /// Per-strategy arm state tracking incremental mean and variance of rewards.
    /// Variance enables Sharpe-like risk adjustment: consistent small wins > erratic big wins.
    /// </summary>
    internal sealed class ArmState
    {
        public int Pulls { get; set; }
        public double MeanReward { get; set; }
        public double VarianceAccumulator { get; set; }

        /// <summary>
        /// Standard deviation of rewards. Returns a floor of 0.001 to avoid division by zero.
        /// </summary>
        public double StdDev => Pulls > 1
            ? Math.Max(Math.Sqrt(VarianceAccumulator / Pulls), 0.001)
            : 0.001;

        /// <summary>
        /// Risk-adjusted value: mean reward / stddev (Sharpe-like ratio).
        /// A strategy earning 0.1% per trade with 0.05% stddev scores higher
        /// than one earning 0.5% with 2% stddev.
        /// </summary>
        public double RiskAdjustedValue => MeanReward / StdDev;

        /// <summary>
        /// Updates the arm with a new reward observation using Welford's online algorithm.
        /// </summary>
        public void Update(double reward)
        {
            Pulls++;
            double delta = reward - MeanReward;
            MeanReward += delta / Pulls;
            double delta2 = reward - MeanReward;
            // Welford's: accumulate sum of squared deviations
            VarianceAccumulator += delta * delta2;
        }
    }

    internal record ArmSnapshot(int Pulls, double MeanReward, double VarianceAccumulator);
}
