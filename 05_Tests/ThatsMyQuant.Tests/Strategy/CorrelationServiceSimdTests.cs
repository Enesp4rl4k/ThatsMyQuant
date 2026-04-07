using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class CorrelationServiceSimdTests
{
    [Fact]
    public void CalculateCorrelationMatrix_PerfectCorrelation_Returns1()
    {
        var series = new[]
        {
            new double[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            new double[] { 2, 4, 6, 8, 10, 12, 14, 16 }, // Perfect positive correlation
        };

        var matrix = CorrelationService.CalculateCorrelationMatrix(series);

        matrix[0, 0].Should().BeApproximately(1.0, 0.001);
        matrix[1, 1].Should().BeApproximately(1.0, 0.001);
        matrix[0, 1].Should().BeApproximately(1.0, 0.001);
        matrix[1, 0].Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void CalculateCorrelationMatrix_Uncorrelated_ReturnsNearZero()
    {
        var series = new[]
        {
            new double[] { 1, -1, 1, -1, 1, -1, 1, -1 },
            new double[] { 1, 1, -1, -1, 1, 1, -1, -1 },
        };

        var matrix = CorrelationService.CalculateCorrelationMatrix(series);

        Math.Abs(matrix[0, 1]).Should().BeLessThan(0.5);
    }

    [Fact]
    public void CalculateCorrelationMatrix_MultipleAssets_SymmetricMatrix()
    {
        var rng = new Random(42);
        var series = new double[4][];
        for (int i = 0; i < 4; i++)
        {
            series[i] = new double[100];
            for (int j = 0; j < 100; j++)
                series[i][j] = rng.NextDouble() * 100;
        }

        var matrix = CorrelationService.CalculateCorrelationMatrix(series);

        // Verify symmetry
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                matrix[i, j].Should().BeApproximately(matrix[j, i], 0.0001);
            }
            matrix[i, i].Should().BeApproximately(1.0, 0.001);
        }
    }

    [Fact]
    public void SimdSum_MatchesLinqSum()
    {
        var data = Enumerable.Range(1, 1000).Select(i => (double)i).ToArray();

        var simdResult = CorrelationService.SimdSum(data);
        var linqResult = data.Sum();

        simdResult.Should().BeApproximately(linqResult, 0.001);
    }

    [Fact]
    public void SimdSum_EmptyArray_ReturnsZero()
    {
        CorrelationService.SimdSum(Array.Empty<double>()).Should().Be(0);
    }

    [Fact]
    public void SimdSubtract_ProducesCorrectResult()
    {
        var a = new double[] { 10, 20, 30, 40, 50, 60, 70, 80 };
        var b = new double[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var result = new double[8];

        CorrelationService.SimdSubtract(a, b, result);

        result.Should().BeEquivalentTo(new double[] { 9, 18, 27, 36, 45, 54, 63, 72 });
    }
}
