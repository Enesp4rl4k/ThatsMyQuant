using FluentAssertions;
using ThatsMyQuant.Application.MachineLearning;
using Xunit;

namespace ThatsMyQuant.Tests.MachineLearning;

/// <summary>
/// Unit tests for FeatureEngineeringAgent.
/// Covers all 5 tensor features and the SafeFloat NaN guard.
/// </summary>
public sealed class FeatureEngineeringAgentTests
{
    // ─── helpers ─────────────────────────────────────────────────────────────

    /// <summary>Feeds N ticks of the same price (flat market).</summary>
    private static FeatureEngineeringAgent FeedFlat(double price, int count)
    {
        var agent = new FeatureEngineeringAgent();
        for (int i = 0; i < count; i++)
            agent.ProcessTickNatively(price);
        return agent;
    }

    /// <summary>Feeds a rising series from startPrice to endPrice over count ticks.</summary>
    private static FeatureEngineeringAgent FeedRising(double startPrice, double endPrice, int count)
    {
        var agent = new FeatureEngineeringAgent();
        for (int i = 0; i < count; i++)
        {
            double t = count > 1 ? (double)i / (count - 1) : 0;
            agent.ProcessTickNatively(startPrice + (endPrice - startPrice) * t);
        }
        return agent;
    }

    // ─── Feature[0] — RSI / 100 ──────────────────────────────────────────────

    [Fact]
    public void Feature0_ReturnsNeutral_BeforeWarmup()
    {
        // RSI needs 14 ticks to warm up; before that OnlineMathHelper returns 50
        var agent = new FeatureEngineeringAgent();
        agent.ProcessTickNatively(100);

        agent.CurrentTensor[0].Should().BeApproximately(0.5f, 0.01f,
            "RSI defaults to 50 (0.5 normalised) during warmup");
    }

    [Fact]
    public void Feature0_HighRsi_AfterStrongRisingMarket()
    {
        // 50 rising ticks → RSI should be well above 50 → feature[0] > 0.5
        var agent = FeedRising(100, 200, 50);

        agent.CurrentTensor[0].Should().BeGreaterThan(0.5f,
            "sustained uptrend should produce RSI > 50");
    }

    [Fact]
    public void Feature0_LowRsi_AfterStrongFallingMarket()
    {
        // 50 falling ticks → RSI should be well below 50 → feature[0] < 0.5
        var agent = FeedRising(200, 100, 50);

        agent.CurrentTensor[0].Should().BeLessThan(0.5f,
            "sustained downtrend should produce RSI < 50");
    }

    [Fact]
    public void Feature0_AlwaysBetweenZeroAndOne()
    {
        var agent = FeedRising(100, 500, 60);

        agent.CurrentTensor[0].Should().BeInRange(0f, 1f);
    }

    // ─── Feature[1] — Price / EMA(9) ─────────────────────────────────────────

    [Fact]
    public void Feature1_EqualsOne_WhenPriceEqualsEma()
    {
        // After many ticks of the same price, EMA converges to that price
        var agent = FeedFlat(100, 50);

        agent.CurrentTensor[1].Should().BeApproximately(1.0f, 0.001f,
            "price/EMA should converge to 1.0 on a flat series");
    }

    [Fact]
    public void Feature1_GreaterThanOne_WhenPriceAboveEma()
    {
        // Sharp jump at the end: price is above the slow EMA
        var agent = FeedFlat(100, 30);
        agent.ProcessTickNatively(150); // spike up

        agent.CurrentTensor[1].Should().BeGreaterThan(1.0f,
            "price spike above EMA should give ratio > 1");
    }

    [Fact]
    public void Feature1_LessThanOne_WhenPriceBelowEma()
    {
        var agent = FeedFlat(100, 30);
        agent.ProcessTickNatively(60); // spike down

        agent.CurrentTensor[1].Should().BeLessThan(1.0f,
            "price drop below EMA should give ratio < 1");
    }

    // ─── Feature[2] — Realized volatility ───────────────────────────────────

    [Fact]
    public void Feature2_IsZero_BeforeVolatilityWarmup()
    {
        // Warmup requires 20 returns; first 19 ticks = 0 returns counted
        var agent = new FeatureEngineeringAgent();
        for (int i = 0; i < 10; i++)
            agent.ProcessTickNatively(100 + i);

        agent.CurrentTensor[2].Should().Be(0f,
            "volatility feature stays 0 until 20 returns are collected");
    }

    [Fact]
    public void Feature2_IsZero_ForFlatMarket()
    {
        // Zero-movement → all returns = 0 → variance = 0
        var agent = FeedFlat(100, 50);

        agent.CurrentTensor[2].Should().BeApproximately(0f, 0.001f,
            "flat market has zero realized volatility");
    }

    [Fact]
    public void Feature2_IsPositive_ForVolatileMarket()
    {
        // Alternating large swings → high return stddev
        var agent = new FeatureEngineeringAgent();
        for (int i = 0; i < 50; i++)
            agent.ProcessTickNatively(i % 2 == 0 ? 100.0 : 110.0); // ±10% swings

        agent.CurrentTensor[2].Should().BeGreaterThan(0f,
            "oscillating market should produce positive realized volatility");
    }

    [Fact]
    public void Feature2_ClampedAtOne_ForExtremeVolatility()
    {
        // Returns way beyond MaxVolatility (10%) → clamped to 1.0
        var agent = new FeatureEngineeringAgent();
        for (int i = 0; i < 50; i++)
            agent.ProcessTickNatively(i % 2 == 0 ? 100.0 : 200.0); // ±100% swings

        agent.CurrentTensor[2].Should().BeApproximately(1.0f, 0.001f,
            "extreme volatility should be clamped to 1.0");
    }

    // ─── Feature[3] — OFI (injected externally) ──────────────────────────────

    [Fact]
    public void Feature3_DefaultsToZero()
    {
        var agent = new FeatureEngineeringAgent();
        agent.ProcessTickNatively(100);

        agent.CurrentTensor[3].Should().Be(0f,
            "OFI should default to 0 until ApplyOrderFlowImbalance is called");
    }

    [Fact]
    public void Feature3_ReflectsInjectedOfi()
    {
        var agent = new FeatureEngineeringAgent();
        agent.ApplyOrderFlowImbalance(0.75);

        agent.CurrentTensor[3].Should().BeApproximately(0.75f, 0.0001f);
    }

    [Fact]
    public void Feature3_AcceptsNegativeOfi()
    {
        var agent = new FeatureEngineeringAgent();
        agent.ApplyOrderFlowImbalance(-0.4);

        agent.CurrentTensor[3].Should().BeApproximately(-0.4f, 0.0001f);
    }

    [Fact]
    public void Feature3_UnchangedByProcessTick()
    {
        var agent = new FeatureEngineeringAgent();
        agent.ApplyOrderFlowImbalance(0.6);

        // Additional price ticks must NOT overwrite the OFI slot
        agent.ProcessTickNatively(100);
        agent.ProcessTickNatively(101);

        agent.CurrentTensor[3].Should().BeApproximately(0.6f, 0.0001f,
            "ProcessTickNatively must not clobber the OFI slot");
    }

    // ─── Feature[4] — Price momentum ─────────────────────────────────────────

    [Fact]
    public void Feature4_EqualsOne_WhenPriceUnchanged()
    {
        var agent = FeedFlat(100, 10);

        agent.CurrentTensor[4].Should().BeApproximately(1.0f, 0.001f,
            "flat market → price / oldest_price = 1.0");
    }

    [Fact]
    public void Feature4_GreaterThanOne_WhenPriceRising()
    {
        var agent = FeedRising(100, 150, 50);

        agent.CurrentTensor[4].Should().BeGreaterThan(1.0f,
            "rising market → current price > oldest price");
    }

    [Fact]
    public void Feature4_LessThanOne_WhenPriceFalling()
    {
        var agent = FeedRising(150, 100, 50);

        agent.CurrentTensor[4].Should().BeLessThan(1.0f,
            "falling market → current price < oldest price");
    }

    // ─── GetSnapshot isolation ───────────────────────────────────────────────

    [Fact]
    public void GetSnapshot_ReturnsCopy_NotReference()
    {
        var agent = FeedFlat(100, 10);
        var snap1 = agent.GetSnapshot();

        // Mutate the snapshot — should not affect CurrentTensor
        snap1[0] = 999f;
        agent.ProcessTickNatively(100);

        agent.CurrentTensor[0].Should().NotBe(999f,
            "GetSnapshot must return an independent copy");
    }

    [Fact]
    public void GetSnapshot_Length_IsAlwaysFive()
    {
        var agent = new FeatureEngineeringAgent();
        agent.GetSnapshot().Should().HaveCount(5);
    }

    // ─── SafeFloat guard ─────────────────────────────────────────────────────

    [Fact]
    public void SafeFloat_ReturnsFallback_ForNaN()
    {
        FeatureEngineeringAgent.SafeFloat(double.NaN, 0.5f).Should().Be(0.5f);
    }

    [Fact]
    public void SafeFloat_ReturnsFallback_ForPositiveInfinity()
    {
        FeatureEngineeringAgent.SafeFloat(double.PositiveInfinity, 1.0f).Should().Be(1.0f);
    }

    [Fact]
    public void SafeFloat_ReturnsFallback_ForNegativeInfinity()
    {
        FeatureEngineeringAgent.SafeFloat(double.NegativeInfinity, -1.0f).Should().Be(-1.0f);
    }

    [Fact]
    public void SafeFloat_ReturnsValue_ForNormalDouble()
    {
        FeatureEngineeringAgent.SafeFloat(0.75, 0f).Should().BeApproximately(0.75f, 0.0001f);
    }

    [Fact]
    public void SafeFloat_HandlesZero()
    {
        FeatureEngineeringAgent.SafeFloat(0.0, 99f).Should().Be(0f);
    }
}
