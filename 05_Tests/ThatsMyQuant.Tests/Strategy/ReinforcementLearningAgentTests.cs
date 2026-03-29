using FluentAssertions;
using ThatsMyQuant.Infrastructure.Services.MetaLearning;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

// ════════════════════════════════════════════════════════════════════════════
//  ReinforcementLearningAgent — UCB1 + Softmax unit tests
// ════════════════════════════════════════════════════════════════════════════

public class ReinforcementLearningAgentTests
{
    // ── ArmState ──────────────────────────────────────────────────────────

    [Fact]
    public void ArmState_Update_TracksIncrementalMean()
    {
        var arm = new ReinforcementLearningAgent.ArmState();
        arm.Update(0.10);
        arm.Update(0.20);
        arm.Update(0.30);

        arm.Pulls.Should().Be(3);
        arm.MeanReward.Should().BeApproximately(0.20, 1e-10);
    }

    [Fact]
    public void ArmState_Update_TracksVariance()
    {
        var arm = new ReinforcementLearningAgent.ArmState();
        // Constant rewards → variance should be ~0
        arm.Update(0.05);
        arm.Update(0.05);
        arm.Update(0.05);

        arm.StdDev.Should().BeApproximately(0.001, 0.001, "constant rewards should have near-zero stddev (floored)");
    }

    [Fact]
    public void ArmState_RiskAdjustedValue_FavorsConsistentWins()
    {
        // Strategy A: small consistent wins (0.1% avg, low variance)
        var consistent = new ReinforcementLearningAgent.ArmState();
        consistent.Update(0.001);
        consistent.Update(0.001);
        consistent.Update(0.001);
        consistent.Update(0.001);

        // Strategy B: volatile wins (0.2% avg, high variance)
        var volatile_ = new ReinforcementLearningAgent.ArmState();
        volatile_.Update(0.010);
        volatile_.Update(-0.006);
        volatile_.Update(0.008);
        volatile_.Update(-0.004);

        // Consistent should have higher risk-adjusted value despite lower raw mean
        consistent.RiskAdjustedValue.Should().BeGreaterThan(volatile_.RiskAdjustedValue,
            "consistent small wins should score higher than volatile larger wins");
    }

    // ── UCB Score ─────────────────────────────────────────────────────────

    [Fact]
    public void ComputeUcbScore_ExplorationBonus_DecreasesWithMorePulls()
    {
        var fewPulls = new ReinforcementLearningAgent.ArmState();
        for (int i = 0; i < 15; i++) fewPulls.Update(0.01);

        var manyPulls = new ReinforcementLearningAgent.ArmState();
        for (int i = 0; i < 200; i++) manyPulls.Update(0.01);

        int totalPulls = 500;
        var scoreFew = ReinforcementLearningAgent.ComputeUcbScore(fewPulls, totalPulls);
        var scoreMany = ReinforcementLearningAgent.ComputeUcbScore(manyPulls, totalPulls);

        // Same mean reward, but fewer pulls should get higher exploration bonus
        scoreFew.Should().BeGreaterThan(scoreMany,
            "under-sampled arm should get exploration bonus");
    }

    // ── Softmax ───────────────────────────────────────────────────────────

    [Fact]
    public void Softmax_SumsToOne()
    {
        var scores = new[] { 1.0, 2.0, 3.0, 0.5 };
        var result = ReinforcementLearningAgent.Softmax(scores, 0.5);

        result.Sum().Should().BeApproximately(1.0, 1e-10);
    }

    [Fact]
    public void Softmax_HigherScoreGetsMoreWeight()
    {
        var scores = new[] { 1.0, 5.0, 2.0 };
        var result = ReinforcementLearningAgent.Softmax(scores, 0.5);

        result[1].Should().BeGreaterThan(result[0], "score 5.0 should outweigh score 1.0");
        result[1].Should().BeGreaterThan(result[2], "score 5.0 should outweigh score 2.0");
    }

    [Fact]
    public void Softmax_LowerTemperature_IncreasesConcentration()
    {
        var scores = new[] { 1.0, 3.0 };
        var hot = ReinforcementLearningAgent.Softmax(scores, 2.0);
        var cold = ReinforcementLearningAgent.Softmax(scores, 0.1);

        // Lower temperature → winner takes more
        cold[1].Should().BeGreaterThan(hot[1],
            "lower temperature should concentrate weight on the highest score");
    }

    [Fact]
    public void Softmax_SingleElement_ReturnsOne()
    {
        var result = ReinforcementLearningAgent.Softmax([42.0], 0.5);
        result.Should().HaveCount(1);
        result[0].Should().BeApproximately(1.0, 1e-10);
    }

    [Fact]
    public void Softmax_Empty_ReturnsEmpty()
    {
        var result = ReinforcementLearningAgent.Softmax([], 0.5);
        result.Should().BeEmpty();
    }

    // ── ComputeUcbWeights ─────────────────────────────────────────────────

    [Fact]
    public void ComputeUcbWeights_WarmupArms_GetNeutralWeight()
    {
        var arms = new Dictionary<string, ReinforcementLearningAgent.ArmState>
        {
            ["Momentum"] = CreateArm(5, 0.01),  // below warmup threshold
            ["Breakout"] = CreateArm(3, 0.02),   // below warmup threshold
        };

        var weights = ReinforcementLearningAgent.ComputeUcbWeights(arms, 8);

        weights["Momentum"].Should().Be(1.0f, "warmup arms should get neutral weight");
        weights["Breakout"].Should().Be(1.0f, "warmup arms should get neutral weight");
    }

    [Fact]
    public void ComputeUcbWeights_MatureArms_DifferentiateByPerformance()
    {
        var arms = new Dictionary<string, ReinforcementLearningAgent.ArmState>
        {
            ["Winner"] = CreateArm(50, 0.02),    // good performer
            ["Loser"] = CreateArm(50, -0.01),     // bad performer
        };

        var weights = ReinforcementLearningAgent.ComputeUcbWeights(arms, 100);

        weights["Winner"].Should().BeGreaterThan(weights["Loser"],
            "better-performing strategy should get higher weight");
    }

    [Fact]
    public void ComputeUcbWeights_MatureWeights_AverageNearOne()
    {
        // When all arms are mature, softmax × armCount should average ~1.0
        var arms = new Dictionary<string, ReinforcementLearningAgent.ArmState>
        {
            ["A"] = CreateArm(30, 0.01),
            ["B"] = CreateArm(30, 0.02),
            ["C"] = CreateArm(30, -0.005),
        };

        var weights = ReinforcementLearningAgent.ComputeUcbWeights(arms, 90);
        var avg = weights.Values.Average();

        avg.Should().BeApproximately(1.0f, 0.01f,
            "average mature weight should be near 1.0 (softmax × armCount normalization)");
    }

    [Fact]
    public void ComputeUcbWeights_MixedMaturity_WarmupStaysNeutral()
    {
        var arms = new Dictionary<string, ReinforcementLearningAgent.ArmState>
        {
            ["Mature"] = CreateArm(50, 0.02),
            ["NewKid"] = CreateArm(3, 0.05),  // great results but too few pulls
        };

        var weights = ReinforcementLearningAgent.ComputeUcbWeights(arms, 53);

        weights["NewKid"].Should().Be(1.0f, "warmup arm should not get boosted weight yet");
        weights["Mature"].Should().BeGreaterThan(0f, "mature arm should have a data-driven weight");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static ReinforcementLearningAgent.ArmState CreateArm(int pulls, double meanReward)
    {
        var arm = new ReinforcementLearningAgent.ArmState();
        // Simulate trades with slight variance around the mean
        var rng = new Random(42);
        for (int i = 0; i < pulls; i++)
        {
            arm.Update(meanReward + (rng.NextDouble() - 0.5) * 0.002);
        }
        return arm;
    }
}
