using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class PboAnalyzerTests
{
    [Fact]
    public void Analyze_ConsistentEdge_LowPbo()
    {
        // Config 0: consistent positive Sharpe across all partitions
        // Config 1: mediocre across all partitions
        int partitions = 8;
        int configs = 5;
        var matrix = new double[partitions, configs];
        var rng = new Random(42);

        for (int s = 0; s < partitions; s++)
        {
            matrix[s, 0] = 2.0 + rng.NextDouble() * 0.5;  // Strong and consistent
            for (int j = 1; j < configs; j++)
            {
                matrix[s, j] = 0.5 + rng.NextDouble() * 0.3; // Mediocre
            }
        }

        var result = PboAnalyzer.Analyze(matrix);

        result.Pbo.Should().BeLessThan(0.30);
        result.IsOverfitted.Should().BeFalse();
        result.TrialCount.Should().BeGreaterThan(0);
        result.Verdict.Should().NotBeEmpty();
    }

    [Fact]
    public void Analyze_OverfittedStrategy_HighPbo()
    {
        // All configs have random IS performance, IS-best rarely wins OOS
        int partitions = 8;
        int configs = 10;
        var matrix = new double[partitions, configs];
        var rng = new Random(123);

        for (int s = 0; s < partitions; s++)
        {
            for (int j = 0; j < configs; j++)
            {
                matrix[s, j] = rng.NextDouble() * 2.0 - 1.0; // Random [-1, 1]
            }
        }

        var result = PboAnalyzer.Analyze(matrix);

        // With purely random data, PBO should be near 0.5 (coin flip)
        result.Pbo.Should().BeGreaterThan(0.30);
        result.TrialCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Analyze_InsufficientData_ReturnsVerdict()
    {
        var matrix = new double[2, 2]; // Too few partitions
        var result = PboAnalyzer.Analyze(matrix);
        result.Verdict.Should().Contain("Insufficient");
    }

    [Fact]
    public void AnalyzeFromReturns_WithRealReturnSeries()
    {
        var goodReturns = Enumerable.Range(0, 100).Select(i => 0.01m + (decimal)(new Random(i).NextDouble() * 0.005)).ToList();
        var badReturns = Enumerable.Range(0, 100).Select(i => -0.005m + (decimal)(new Random(i + 1000).NextDouble() * 0.01)).ToList();

        var configs = new List<IReadOnlyList<decimal>> { goodReturns, badReturns };
        var result = PboAnalyzer.AnalyzeFromReturns(configs, partitionCount: 8);

        result.TrialCount.Should().BeGreaterThan(0);
        result.Pbo.Should().BeGreaterThanOrEqualTo(0);
        result.Pbo.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void AnalyzeFromReturns_SingleConfig_ReturnsVerdict()
    {
        var configs = new List<IReadOnlyList<decimal>>
        {
            new List<decimal> { 1m, 2m, 3m }
        };
        var result = PboAnalyzer.AnalyzeFromReturns(configs);
        result.Verdict.Should().Contain("Need at least 2");
    }

    [Fact]
    public void Analyze_MaxTrialsCapRespected()
    {
        int partitions = 16;
        int configs = 3;
        var matrix = new double[partitions, configs];
        var rng = new Random(7);
        for (int s = 0; s < partitions; s++)
            for (int j = 0; j < configs; j++)
                matrix[s, j] = rng.NextDouble();

        var result = PboAnalyzer.Analyze(matrix, maxTrials: 50);
        result.TrialCount.Should().BeLessThanOrEqualTo(50 + 20); // Some tolerance for C(16,8)
    }
}
