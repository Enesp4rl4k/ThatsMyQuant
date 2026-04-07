using FluentAssertions;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Structural;

/// <summary>
/// Sprint 3 Preservation Tests.
/// These tests PASS on unfixed code — they assert behaviours that must be preserved
/// after the Sprint 3 hot-path performance fixes (Tasks 18, 19, 20).
/// Validates: Requirements 3.5, 3.6
/// </summary>
public class Sprint3PreservationTests
{
    /// <summary>
    /// P1: ProcessTickNatively(double.NaN) must not propagate NaN into CurrentTensor.
    /// Every element must be 0f or 1f (the SafeFloat fallback values used in the implementation).
    /// Validates: Requirements 3.5
    /// </summary>
    [Fact]
    public void P1_ProcessTickNatively_WithNaN_DoesNotPropagatePoisonValues()
    {
        var agent = new FeatureEngineeringAgent();

        agent.ProcessTickNatively(double.NaN);

        for (int i = 0; i < agent.CurrentTensor.Length; i++)
        {
            var value = agent.CurrentTensor[i];
            float.IsNaN(value).Should().BeFalse(
                because: $"CurrentTensor[{i}] = {value} — SafeFloat guards must prevent NaN " +
                         "from propagating into the ML feature tensor");
            float.IsInfinity(value).Should().BeFalse(
                because: $"CurrentTensor[{i}] = {value} — SafeFloat guards must prevent Infinity " +
                         "from propagating into the ML feature tensor");
        }
    }

    /// <summary>
    /// P2: CalculateBatchSMA must return early (no writes) when prices.Length &lt; windowSize.
    /// results[0] must remain 0f (the default value of a freshly allocated float[]).
    /// Validates: Requirements 3.6
    /// </summary>
    [Fact]
    public void P2_CalculateBatchSMA_EarlyExit_WhenPricesLengthLessThanWindowSize()
    {
        float[] prices = [1f, 2f];
        float[] results = new float[2]; // default-initialised to 0f

        VectorizedMathUtils.CalculateBatchSMA(prices, windowSize: 5, results: results);

        results[0].Should().Be(0f,
            because: "prices.Length (2) < windowSize (5), so CalculateBatchSMA must return " +
                     "immediately without writing any values into the results span");
    }

    /// <summary>
    /// P3: CalculateBatchSMA correctness baseline — [1,2,3,4,5] with windowSize=3
    /// must produce [2f, 3f, 4f]. This is the expected output that the O(N) fix must preserve.
    /// Validates: Requirements 3.6
    /// </summary>
    [Fact]
    public void P3_CalculateBatchSMA_CorrectnessBaseline_ProducesExpectedAverages()
    {
        float[] prices = [1f, 2f, 3f, 4f, 5f];
        float[] results = new float[3];

        VectorizedMathUtils.CalculateBatchSMA(prices, windowSize: 3, results: results);

        results[0].Should().BeApproximately(2f, precision: 1e-5f,
            because: "SMA of [1,2,3] = 6/3 = 2");
        results[1].Should().BeApproximately(3f, precision: 1e-5f,
            because: "SMA of [2,3,4] = 9/3 = 3");
        results[2].Should().BeApproximately(4f, precision: 1e-5f,
            because: "SMA of [3,4,5] = 12/3 = 4");
    }
}
