using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public sealed class MonteCarloSimulatorTests
{
    [Fact]
    public async Task RunSimulationsAsync_FinalEquityHistogram_CountsSumToIterations()
    {
        var sim = new MonteCarloSimulator();
        var pnls = new List<double> { -120, 45, 200, -30, 80 };
        const int iterations = 800;

        var result = await sim.RunSimulationsAsync(pnls, 100_000m, iterations);

        result.FinalEquityHistogram.Sum(b => b.Count).Should().Be(iterations);
    }
}
