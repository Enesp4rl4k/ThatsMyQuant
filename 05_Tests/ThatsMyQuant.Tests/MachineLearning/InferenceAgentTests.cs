using FluentAssertions;
using ThatsMyQuant.Application.MachineLearning;
using Xunit;

namespace ThatsMyQuant.Tests.MachineLearning;

/// <summary>
/// Unit tests for InferenceAgent.
/// Since no trained model file exists in the test environment, these tests verify:
/// - Graceful degradation when model is missing
/// - Input validation (null, wrong length, NaN/Infinity guards)
/// - PredictionResult structure correctness
/// </summary>
public sealed class InferenceAgentTests
{
    /// <summary>Creates an agent with no model file → _isAvailable = false.</summary>
    private static InferenceAgent CreateAgentWithoutModel()
        => new InferenceAgent("nonexistent-model-path.zip");

    // ─── Model missing fallback ──────────────────────────────────────────────

    [Fact]
    public void PredictNextReturn_NoModel_ReturnsInvalid()
    {
        var agent = CreateAgentWithoutModel();
        var result = agent.PredictNextReturn(new float[] { 0.5f, 1.0f, 0.3f, 0.1f, 1.02f });

        result.IsValid.Should().BeFalse("no model file → predictions should be invalid");
        result.Value.Should().Be(0f, "fallback value should be 0");
    }

    [Fact]
    public void PredictNextReturn_NoModel_DoesNotThrow()
    {
        var agent = CreateAgentWithoutModel();

        var act = () => agent.PredictNextReturn(new float[] { 0.5f, 1.0f, 0.3f, 0.1f, 1.02f });

        act.Should().NotThrow("missing model must degrade gracefully, never throw");
    }

    // ─── Input validation ────────────────────────────────────────────────────

    [Fact]
    public void PredictNextReturn_NullInput_ReturnsInvalid()
    {
        var agent = CreateAgentWithoutModel();
        var result = agent.PredictNextReturn(null!);

        result.IsValid.Should().BeFalse("null features should return invalid");
        result.Value.Should().Be(0f);
    }

    [Fact]
    public void PredictNextReturn_WrongLength_ReturnsInvalid()
    {
        var agent = CreateAgentWithoutModel();

        agent.PredictNextReturn(new float[] { 0.5f, 1.0f }).IsValid
            .Should().BeFalse("features array must be exactly 5 elements");

        agent.PredictNextReturn(new float[] { 0.5f, 1.0f, 0.3f, 0.1f, 1.02f, 0.5f }).IsValid
            .Should().BeFalse("6 elements should also be rejected");
    }

    [Fact]
    public void PredictNextReturn_EmptyArray_ReturnsInvalid()
    {
        var agent = CreateAgentWithoutModel();
        var result = agent.PredictNextReturn(Array.Empty<float>());

        result.IsValid.Should().BeFalse("empty array is wrong length");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PredictNextReturn_NaNInAnyFeature_ReturnsInvalid(int poisonIndex)
    {
        // Even with a model, NaN features should be caught by the guard
        var agent = CreateAgentWithoutModel();
        var features = new float[] { 0.5f, 1.0f, 0.3f, 0.1f, 1.02f };
        features[poisonIndex] = float.NaN;

        var result = agent.PredictNextReturn(features);

        result.IsValid.Should().BeFalse($"NaN in feature[{poisonIndex}] should be rejected");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PredictNextReturn_InfinityInAnyFeature_ReturnsInvalid(int poisonIndex)
    {
        var agent = CreateAgentWithoutModel();
        var features = new float[] { 0.5f, 1.0f, 0.3f, 0.1f, 1.02f };
        features[poisonIndex] = float.PositiveInfinity;

        var result = agent.PredictNextReturn(features);

        result.IsValid.Should().BeFalse($"Infinity in feature[{poisonIndex}] should be rejected");
    }

    [Fact]
    public void PredictNextReturn_NegativeInfinityInFeature_ReturnsInvalid()
    {
        var agent = CreateAgentWithoutModel();
        var features = new float[] { 0.5f, float.NegativeInfinity, 0.3f, 0.1f, 1.02f };

        agent.PredictNextReturn(features).IsValid.Should().BeFalse();
    }

    // ─── PredictionResult struct ─────────────────────────────────────────────

    [Fact]
    public void PredictionResult_DefaultValues_AreZeroAndInvalid()
    {
        var result = new PredictionResult();

        result.Value.Should().Be(0f);
        result.IsValid.Should().BeFalse();
        result.Confidence.Should().Be(0f);
    }

    [Fact]
    public void PredictionResult_CanSetAllFields()
    {
        var result = new PredictionResult
        {
            Value = 0.05f,
            IsValid = true,
            Confidence = 0.8f
        };

        result.Value.Should().Be(0.05f);
        result.IsValid.Should().BeTrue();
        result.Confidence.Should().Be(0.8f);
    }

    // ─── Constructor robustness ──────────────────────────────────────────────

    [Fact]
    public void Constructor_InvalidModelPath_DoesNotThrow()
    {
        var act = () => new InferenceAgent("totally/fake/path/model.zip");

        act.Should().NotThrow("missing model path should be handled gracefully");
    }

    [Fact]
    public void Constructor_EmptyModelPath_DoesNotThrow()
    {
        var act = () => new InferenceAgent("");

        act.Should().NotThrow("empty model path should degrade to unavailable");
    }

    // ─── Multiple calls stability ────────────────────────────────────────────

    [Fact]
    public void PredictNextReturn_MultipleCalls_AllReturnConsistentInvalid()
    {
        var agent = CreateAgentWithoutModel();
        var features = new float[] { 0.5f, 1.0f, 0.3f, 0.1f, 1.02f };

        // Calling predict many times should not degrade or throw
        for (int i = 0; i < 100; i++)
        {
            var result = agent.PredictNextReturn(features);
            result.IsValid.Should().BeFalse();
            result.Value.Should().Be(0f);
        }
    }
}
