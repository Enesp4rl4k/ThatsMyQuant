using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class MafeAnalyzerTests
{
    [Fact]
    public void ComputeForTrade_BuySide_TracksMaxDipAndPeak()
    {
        var bars = new List<Candle>
        {
            MakeCandle(99m, 102m, 98m, 101m),   // Low=98 (adverse 2), High=102 (favorable 2)
            MakeCandle(101m, 105m, 100m, 104m),  // Low=100, High=105 (favorable 5)
            MakeCandle(104m, 104m, 95m, 96m),    // Low=95 (adverse 5)
        };

        var result = MafeAnalyzer.ComputeForTrade(100m, TradeSide.Buy, bars);

        result.MaxAdverseExcursion.Should().Be(5m);      // 100-95
        result.MaxFavorableExcursion.Should().Be(5m);     // 105-100
        result.MaxAdverseExcursionPercent.Should().Be(5m); // 5/100*100
        result.MaxFavorableExcursionPercent.Should().Be(5m);
    }

    [Fact]
    public void ComputeForTrade_SellSide_InversesAdverseAndFavorable()
    {
        var bars = new List<Candle>
        {
            MakeCandle(100m, 103m, 97m, 98m),  // High=103 (adverse for sell), Low=97 (favorable for sell)
        };

        var result = MafeAnalyzer.ComputeForTrade(100m, TradeSide.Sell, bars);

        result.MaxAdverseExcursion.Should().Be(3m);   // 103-100 (price went against sell)
        result.MaxFavorableExcursion.Should().Be(3m);  // 100-97 (price moved in sell's favor)
    }

    [Fact]
    public void ComputeForTrade_EmptyBars_ReturnsZero()
    {
        var result = MafeAnalyzer.ComputeForTrade(100m, TradeSide.Buy, Array.Empty<Candle>());
        result.MaxAdverseExcursion.Should().Be(0m);
        result.MaxFavorableExcursion.Should().Be(0m);
    }

    [Fact]
    public void ComputeDistribution_ReturnsOptimalLevels()
    {
        var results = Enumerable.Range(1, 20).Select(i => new MafeAnalyzer.MafeResult
        {
            MaxAdverseExcursionPercent = i * 0.5m,   // 0.5% to 10%
            MaxFavorableExcursionPercent = i * 1.0m,  // 1% to 20%
        }).ToList();

        var dist = MafeAnalyzer.ComputeDistribution(results);

        dist.SampleCount.Should().Be(20);
        dist.OptimalStopLossPercent.Should().BeGreaterThan(0m);
        dist.OptimalTakeProfitPercent.Should().BeGreaterThan(0m);
        dist.EdgeRatio.Should().BeGreaterThan(1m); // MFE > MAE = positive edge
        dist.MaeP75.Should().BeLessThan(dist.MaeP90);
    }

    [Fact]
    public void EvaluateExitLevels_TightStopLoss_DetectedAsTooTight()
    {
        var dist = new MafeAnalyzer.MafeDistribution
        {
            SampleCount = 50,
            MaeMedian = 2.0m,
            MaeP75 = 3.5m,
            MaeP90 = 5.0m,
            MaeP95 = 6.5m,
            MfeMedian = 4.0m,
            MfeP75 = 6.0m,
            MfeP90 = 8.0m,
            OptimalStopLossPercent = 3.5m,
            OptimalTakeProfitPercent = 6.0m,
            EdgeRatio = 1.71m
        };

        var adj = MafeAnalyzer.EvaluateExitLevels(1.0m, 10.0m, dist);

        adj.HasSufficientData.Should().BeTrue();
        adj.IsStopLossTooTight.Should().BeTrue();  // 1% < median 2%
        adj.IsTakeProfitTooFar.Should().BeTrue();   // 10% > P90 8%
    }

    [Fact]
    public void EvaluateExitLevels_InsufficientData_ReturnsNoData()
    {
        var dist = new MafeAnalyzer.MafeDistribution { SampleCount = 5 };
        var adj = MafeAnalyzer.EvaluateExitLevels(2m, 5m, dist);
        adj.HasSufficientData.Should().BeFalse();
    }

    private static Candle MakeCandle(decimal open, decimal high, decimal low, decimal close)
        => new()
        {
            Open = open,
            High = high,
            Low = low,
            Close = close,
            Volume = 1000m,
            Timestamp = DateTime.UtcNow
        };
}
