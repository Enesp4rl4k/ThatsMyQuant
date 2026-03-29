using FluentAssertions;
using Xunit;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Tests.MachineLearning;

public class FeatureEngineeringAgentTests
{
    [Fact]
    public void ProcessTickNatively_ShouldProduceValidTensorValues()
    {
        var agent = new FeatureEngineeringAgent();

        // Warm up with enough ticks for RSI/EMA to stabilize
        for (int i = 0; i < 20; i++)
            agent.ProcessTickNatively(100.0 + i * 0.1);

        agent.CurrentTensor[0].Should().BeInRange(0f, 1f, "RSI should be normalized 0-1");
        agent.CurrentTensor[1].Should().BeInRange(0.5f, 1.5f, "EMA deviation should be near 1.0");
        agent.CurrentTensor[2].Should().BeInRange(0.5f, 1.5f, "MA bias should be near 1.0");
    }

    [Fact]
    public void ProcessTickNatively_WithNaNPrice_ShouldNotCorruptTensor()
    {
        var agent = new FeatureEngineeringAgent();

        // Establish baseline
        agent.ProcessTickNatively(100.0);
        var baseline = agent.CurrentTensor[0];

        // Feed NaN — should not crash or produce NaN in tensor
        agent.ProcessTickNatively(double.NaN);

        foreach (var val in agent.CurrentTensor)
        {
            float.IsNaN(val).Should().BeFalse("NaN must never propagate into tensor");
            float.IsInfinity(val).Should().BeFalse("Infinity must never propagate into tensor");
        }
    }

    [Fact]
    public void ProcessTickNatively_WithInfinityPrice_ShouldNotCorruptTensor()
    {
        var agent = new FeatureEngineeringAgent();
        agent.ProcessTickNatively(100.0);

        agent.ProcessTickNatively(double.PositiveInfinity);

        foreach (var val in agent.CurrentTensor)
        {
            float.IsNaN(val).Should().BeFalse("NaN must never propagate into tensor");
            float.IsInfinity(val).Should().BeFalse("Infinity must never propagate into tensor");
        }
    }

    [Fact]
    public void ApplyFoundationData_ShouldWireBidAskImbalanceToFeature3()
    {
        var agent = new FeatureEngineeringAgent();

        var snapshot = new FoundationSnapshot
        {
            BidAskImbalance = 0.75,
            TickDirectionRatio = 0.6
        };

        agent.ApplyFoundationData(snapshot);

        agent.CurrentTensor[3].Should().BeApproximately(0.75f, 0.001f,
            "Feature[3] should reflect BidAskImbalance");
        agent.CurrentTensor[4].Should().BeApproximately(0.6f, 0.001f,
            "Feature[4] should reflect TickDirectionRatio");
    }

    [Fact]
    public void ApplyFoundationData_WithNaNFields_ShouldUseFallbacks()
    {
        var agent = new FeatureEngineeringAgent();

        var snapshot = new FoundationSnapshot
        {
            BidAskImbalance = double.NaN,
            TickDirectionRatio = double.PositiveInfinity
        };

        agent.ApplyFoundationData(snapshot);

        agent.CurrentTensor[3].Should().Be(0f, "NaN BidAskImbalance should fallback to 0");
        agent.CurrentTensor[4].Should().Be(0.5f, "Infinity TickDirectionRatio should fallback to 0.5");
    }

    [Fact]
    public void ApplyFoundationData_EmptySnapshot_ShouldSetNeutralValues()
    {
        var agent = new FeatureEngineeringAgent();

        agent.ApplyFoundationData(FoundationSnapshot.Empty);

        agent.CurrentTensor[3].Should().Be(0f, "Empty BidAskImbalance defaults to 0");
        agent.CurrentTensor[4].Should().Be(0f, "Empty TickDirectionRatio defaults to 0");
    }

    [Fact]
    public void ApplyOrderFlowImbalance_ShouldUpdateFeature3()
    {
        var agent = new FeatureEngineeringAgent();

        agent.ApplyOrderFlowImbalance(0.42);

        agent.CurrentTensor[3].Should().BeApproximately(0.42f, 0.001f);
    }

    [Fact]
    public void ApplyOrderFlowImbalance_WithNaN_ShouldGuard()
    {
        var agent = new FeatureEngineeringAgent();
        agent.ApplyOrderFlowImbalance(0.5);
        agent.ApplyOrderFlowImbalance(double.NaN);

        agent.CurrentTensor[3].Should().Be(0f, "NaN imbalance should guard to 0");
    }

    [Fact]
    public void CopyTo_ShouldProduceIdenticalSnapshot()
    {
        var agent = new FeatureEngineeringAgent();
        agent.ProcessTickNatively(105.0);
        agent.ApplyOrderFlowImbalance(0.3);

        Span<float> dest = stackalloc float[5];
        agent.CopyTo(dest);

        for (int i = 0; i < 5; i++)
            dest[i].Should().Be(agent.CurrentTensor[i]);
    }

    [Fact]
    public void CopyTo_WithTooSmallSpan_ShouldThrow()
    {
        var agent = new FeatureEngineeringAgent();
        var dest = new float[3];

        var act = () => agent.CopyTo(dest.AsSpan());
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(double.NaN, 0f)]
    [InlineData(double.PositiveInfinity, 0f)]
    [InlineData(double.NegativeInfinity, 0f)]
    [InlineData(1.5, 1.5f)]
    [InlineData(-0.3, -0.3f)]
    public void SafeFloat_ShouldClampPoisonValues(double input, float expected)
    {
        FeatureEngineeringAgent.SafeFloat(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(double.NaN, 0.5f, 0.5f)]
    [InlineData(double.PositiveInfinity, 1.0f, 1.0f)]
    [InlineData(42.0, 0.5f, 42.0f)]
    public void SafeFloat_WithFallback_ShouldUseFallbackOnPoison(double input, float fallback, float expected)
    {
        FeatureEngineeringAgent.SafeFloat(input, fallback).Should().Be(expected);
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentWritesShouldNotCorrupt()
    {
        var agent = new FeatureEngineeringAgent();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var tickTask = Task.Run(() =>
        {
            var rng = new Random(42);
            while (!cts.Token.IsCancellationRequested)
                agent.ProcessTickNatively(100 + rng.NextDouble() * 10);
        });

        var ofiTask = Task.Run(() =>
        {
            var rng = new Random(123);
            while (!cts.Token.IsCancellationRequested)
                agent.ApplyOrderFlowImbalance(rng.NextDouble() * 2 - 1);
        });

        var foundationTask = Task.Run(() =>
        {
            var rng = new Random(456);
            while (!cts.Token.IsCancellationRequested)
                agent.ApplyFoundationData(new FoundationSnapshot
                {
                    BidAskImbalance = rng.NextDouble() * 2 - 1,
                    TickDirectionRatio = rng.NextDouble()
                });
        });

        var readTask = Task.Run(() =>
        {
            var buf = new float[5];
            while (!cts.Token.IsCancellationRequested)
            {
                agent.CopyTo(buf.AsSpan());
                for (int i = 0; i < 5; i++)
                {
                    float.IsNaN(buf[i]).Should().BeFalse("NaN must never appear under concurrent access");
                    float.IsInfinity(buf[i]).Should().BeFalse("Infinity must never appear under concurrent access");
                }
            }
        });

        await Task.WhenAll(tickTask, ofiTask, foundationTask, readTask);
    }
}
