using FluentAssertions;
using ThatsMyQuant.Contracts.Options;
using Xunit;

namespace ThatsMyQuant.Tests.MachineLearning;

public sealed class InferenceOptionsTests
{
    [Fact]
    public void GetEffectivePoolSize_WhenPredictionEngineCountSet_UsesExactValue()
    {
        var o = new InferenceOptions { PredictionEngineCount = 4 };
        o.GetEffectivePoolSize().Should().Be(4);
    }

    [Fact]
    public void GetEffectivePoolSize_WhenMaxPredictionEnginesSet_CapsProcessorCount()
    {
        var o = new InferenceOptions { MaxPredictionEngines = 4 };
        o.GetEffectivePoolSize().Should().Be(Math.Min(Environment.ProcessorCount, 4));
    }

    [Fact]
    public void GetEffectivePoolSize_PredictionEngineCountOverridesMax()
    {
        var o = new InferenceOptions { PredictionEngineCount = 8, MaxPredictionEngines = 2 };
        o.GetEffectivePoolSize().Should().Be(8);
    }

    [Fact]
    public void LazyPoolWarmup_DefaultsToTrue()
    {
        new InferenceOptions().LazyPoolWarmup.Should().BeTrue();
    }

    [Fact]
    public void LazyPoolBackgroundLogLevel_DefaultsToWarning()
    {
        new InferenceOptions().LazyPoolBackgroundLogLevel.Should().Be("Warning");
    }
}
