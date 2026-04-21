using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class StrategyRoutersTests
{
    [Fact]
    public void TrendRouter_ShouldWidenTargetAndRunLonger_ForHigherQualityTrend()
    {
        var router = new TrendStrategyRouter();
        var weaker = router.BuildDecision(CreateContext(
            TradeSide.Buy,
            68,
            indicatorFactory: () => new IndicatorSnapshot
            {
                CurrentPrice = 100m,
                EMA20 = 101,
                EMA50 = 100,
                LinearRegression = new LinearRegressionEdgeSnapshot { RSquared = 0.52 }
            }));
        var stronger = router.BuildDecision(CreateContext(
            TradeSide.Buy,
            89,
            indicatorFactory: () => new IndicatorSnapshot
            {
                CurrentPrice = 100m,
                EMA20 = 104,
                EMA50 = 100,
                LinearRegression = new LinearRegressionEdgeSnapshot { RSquared = 0.88 }
            }));

        stronger.ShouldTrade.Should().BeTrue();
        stronger.TakeProfit.Should().BeGreaterThan(weaker.TakeProfit);
        stronger.MaxHoldingBars.Should().BeGreaterThan(weaker.MaxHoldingBars);
        stronger.PartialExitFraction.Should().BeLessThan(weaker.PartialExitFraction);
        stronger.BreakevenTriggerFraction.Should().BeLessThan(weaker.BreakevenTriggerFraction);
    }

    [Fact]
    public void RangeRouter_ShouldTakeMoreOffEarlier_WhenStretchIsExtreme()
    {
        var router = new RangeStrategyRouter();
        var mild = router.BuildDecision(CreateContext(
            TradeSide.Buy,
            74,
            indicatorFactory: () => new IndicatorSnapshot
            {
                CurrentPrice = 100m,
                RSI = 42,
                LinearRegression = new LinearRegressionEdgeSnapshot { SigmaDistance = 0.4 }
            }));
        var stretched = router.BuildDecision(CreateContext(
            TradeSide.Buy,
            74,
            indicatorFactory: () => new IndicatorSnapshot
            {
                CurrentPrice = 100m,
                RSI = 27,
                LinearRegression = new LinearRegressionEdgeSnapshot { SigmaDistance = -1.8 }
            }));

        stretched.ShouldTrade.Should().BeTrue();
        stretched.PartialExitFraction.Should().BeGreaterThan(mild.PartialExitFraction);
        stretched.BreakevenTriggerFraction.Should().BeLessThan(mild.BreakevenTriggerFraction);
        stretched.TakeProfit.Should().BeGreaterThan(mild.TakeProfit);
    }

    [Fact]
    public void BreakoutRouter_ShouldRewardCleanerBreakouts_WithLargerExpansionTarget()
    {
        var router = new BreakoutStrategyRouter();
        var baseline = router.BuildDecision(CreateContext(
            TradeSide.Buy,
            76,
            indicatorFactory: () => new IndicatorSnapshot
            {
                CurrentPrice = 100m,
                VolumeRatio = 1.15,
                MACDHistogram = 0.03,
                LinearRegression = new LinearRegressionEdgeSnapshot { RSquared = 0.46 }
            },
            regime: MarketRegime.Volatile));
        var cleaner = router.BuildDecision(CreateContext(
            TradeSide.Buy,
            88,
            indicatorFactory: () => new IndicatorSnapshot
            {
                CurrentPrice = 100m,
                VolumeRatio = 2.4,
                MACDHistogram = 0.12,
                LinearRegression = new LinearRegressionEdgeSnapshot { RSquared = 0.84 }
            },
            regime: MarketRegime.Volatile));

        cleaner.ShouldTrade.Should().BeTrue();
        cleaner.TakeProfit.Should().BeGreaterThan(baseline.TakeProfit);
        cleaner.StopLoss.Should().BeGreaterThan(baseline.StopLoss);
        cleaner.BreakevenTriggerFraction.Should().BeLessThan(baseline.BreakevenTriggerFraction);
    }

    [Fact]
    public void TrendRouter_ShouldWidenTargets_WhenVenueCostsAreHigher()
    {
        var router = new TrendStrategyRouter();
        var cheap = router.BuildDecision(CreateContext(
            TradeSide.Buy,
            82,
            executionProfile: new MarketExecutionProfile("Crypto", "Cheap", 0.03m, 0.01m, 0.02m, 1_000_000m, 80),
            indicatorFactory: () => new IndicatorSnapshot
            {
                CurrentPrice = 100m,
                EMA20 = 103,
                EMA50 = 100,
                LinearRegression = new LinearRegressionEdgeSnapshot { RSquared = 0.80 }
            }));
        var expensive = router.BuildDecision(CreateContext(
            TradeSide.Buy,
            82,
            executionProfile: new MarketExecutionProfile("Crypto", "Expensive", 0.22m, 0.08m, 0.12m, 1_000_000m, 180),
            indicatorFactory: () => new IndicatorSnapshot
            {
                CurrentPrice = 100m,
                EMA20 = 103,
                EMA50 = 100,
                LinearRegression = new LinearRegressionEdgeSnapshot { RSquared = 0.80 }
            }));

        expensive.ShouldTrade.Should().BeTrue();
        expensive.TakeProfit.Should().BeGreaterThan(cheap.TakeProfit);
        expensive.PartialExitFraction.Should().BeLessThan(cheap.PartialExitFraction);
        expensive.BreakevenTriggerFraction.Should().BeGreaterThan(cheap.BreakevenTriggerFraction);
        expensive.Reasoning.Should().Contain("Execution cost floor");
    }

    private static StrategyRouterContext CreateContext(
        TradeSide side,
        int confidence,
        Func<IndicatorSnapshot> indicatorFactory,
        MarketExecutionProfile? executionProfile = null,
        MarketRegime regime = MarketRegime.Trending)
    {
        var indicators = indicatorFactory();
        var profile = executionProfile ?? new MarketExecutionProfile("Crypto", "Baseline", 0.08m, 0.03m, 0.04m, 1_000_000m, 120);
        return new StrategyRouterContext(
            new Signal { Symbol = "BTC-USD", Side = side, Price = 100m, Source = "TradingView" },
            indicators,
            new MarketRegimeDetector.RegimeResult
            {
                Regime = regime,
                AdaptedRsiPeriod = 14,
                AdaptedEmaFast = 20,
                AdaptedEmaSlow = 50,
                StopLossMultiplier = 2.0,
                TakeProfitMultiplier = 3.0,
                Description = "test"
            },
            profile,
            side,
            confidence,
            "session ok",
            "tradeability ok",
            0.03m,
            100m,
            2m,
            profile.SpreadPercent + (profile.SlippagePercent * 2m) + (profile.CommissionPercentPerSide * 2m),
            ["alpha"]);
    }
}
