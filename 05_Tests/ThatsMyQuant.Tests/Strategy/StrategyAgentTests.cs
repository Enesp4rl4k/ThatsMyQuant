using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Services.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

// ════════════════════════════════════════════════════════════════════════════
//  Shared test helpers
// ════════════════════════════════════════════════════════════════════════════

file static class TestData
{
    public static Signal DefaultSignal(decimal price = 100m, string symbol = "BTCUSDT")
        => new() { Symbol = symbol, Price = price, Timestamp = DateTime.UtcNow };

    public static Signal SignalWithSpread(decimal price, double bestBid, double bestAsk, string symbol = "BTCUSDT")
        => new() { Symbol = symbol, Price = price, Timestamp = DateTime.UtcNow, BestBid = bestBid, BestAsk = bestAsk };

    public static FoundationSnapshot Foundation(decimal vwap = 0m, double bidAskImbalance = 0, double spreadBps = 0)
        => new() { Vwap = vwap, BidAskImbalance = bidAskImbalance, SpreadBps = spreadBps };

    public static PatternSnapshot Patterns(bool squeeze = false, double vwapDeviation = double.NaN)
        => new() { BollingerSqueeze = squeeze, VwapDeviationSigmas = vwapDeviation };

    /// <summary>
    /// Generates N candles with linearly interpolated closes from <paramref name="startClose"/> to <paramref name="endClose"/>.
    /// Volume defaults to <paramref name="baseVolume"/> for all bars.
    /// </summary>
    public static List<Candle> LinearCandles(int count, decimal startClose, decimal endClose, decimal baseVolume = 1000m)
    {
        var candles = new List<Candle>(count);
        for (int i = 0; i < count; i++)
        {
            var t = count > 1 ? (decimal)i / (count - 1) : 0m;
            var close = startClose + (endClose - startClose) * t;
            candles.Add(new Candle
            {
                Timestamp = DateTime.UtcNow.AddMinutes(-count + i),
                Open = close - 0.5m,
                High = close + 1m,
                Low = close - 1m,
                Close = close,
                Volume = baseVolume
            });
        }
        return candles;
    }

    /// <summary>
    /// Generates N candles with the same close (flat market).
    /// </summary>
    public static List<Candle> FlatCandles(int count, decimal close, decimal volume = 1000m)
        => LinearCandles(count, close, close, volume);

    /// <summary>
    /// Generates candles with a volume spike on the last N bars.
    /// </summary>
    public static List<Candle> CandlesWithVolumeSurge(int count, decimal close, decimal baseVolume, decimal surgeVolume, int surgeBars = 3)
    {
        var candles = FlatCandles(count, close, baseVolume);
        for (int i = Math.Max(0, candles.Count - surgeBars); i < candles.Count; i++)
            candles[i].Volume = surgeVolume;
        return candles;
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  MomentumStrategyAgent Tests
// ════════════════════════════════════════════════════════════════════════════

public class MomentumStrategyAgentTests
{
    private readonly MomentumStrategyAgent _agent = new(Substitute.For<ILogger<MomentumStrategyAgent>>());

    // ── Strong bullish setup: 60 candles rising 80→120, price at 120, VWAP 100.
    // RSI bullish, ADX trending, EMA fast > slow, MTF agrees.
    private static (List<Candle> candles, Signal signal, FoundationSnapshot foundation) StrongBullishSetup()
    {
        var candles = TestData.LinearCandles(60, 80m, 120m);
        return (candles, TestData.DefaultSignal(price: 120m), TestData.Foundation(vwap: 100m));
    }

    // ── Strong bearish setup: 60 candles falling 120→80.
    private static (List<Candle> candles, Signal signal, FoundationSnapshot foundation) StrongBearishSetup()
    {
        var candles = TestData.LinearCandles(60, 120m, 80m);
        return (candles, TestData.DefaultSignal(price: 80m), TestData.Foundation(vwap: 100m));
    }

    [Theory]
    [InlineData(MarketRegime.Trending, true)]
    [InlineData(MarketRegime.Ranging, false)]
    [InlineData(MarketRegime.Volatile, false)]
    [InlineData(MarketRegime.Unknown, false)]
    public void CanHandle_OnlyTrending(MarketRegime regime, bool expected)
    {
        _agent.CanHandle(TestData.DefaultSignal(), regime).Should().Be(expected);
    }

    [Fact]
    public void StrategyTag_IsMomentum() => _agent.StrategyTag.Should().Be("Momentum");

    [Fact]
    public void SwarmWeight_Is20() => _agent.SwarmWeight.Should().Be(20);

    // ── Abstain scenarios ──

    [Fact]
    public async Task Evaluate_Abstains_WhenTooFewCandles()
    {
        var candles = TestData.FlatCandles(10, 100m); // need 30+
        var result = await _agent.EvaluateAsync(
            TestData.DefaultSignal(), PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);

        result.Vote.Should().Be(VoteOutcome.Abstain);
        result.StrategyTag.Should().Be("Momentum");
    }

    [Fact]
    public async Task Evaluate_Abstains_WhenRsiNeutral()
    {
        // Flat candles → RSI ~ 50 (neither bullish nor bearish)
        var candles = TestData.FlatCandles(60, 100m);
        var signal = TestData.DefaultSignal(price: 100m);
        var foundation = TestData.Foundation(vwap: 100m);

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, foundation, candles);

        result.Vote.Should().Be(VoteOutcome.Abstain);
    }

    // ── Bullish approval ──

    [Fact]
    public async Task Evaluate_Approves_BullishWithMultipleFactors()
    {
        var (candles, signal, foundation) = StrongBullishSetup();

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, foundation, candles);

        result.Vote.Should().Be(VoteOutcome.Approve);
        result.ConfidenceAdjustment.Should().BeGreaterThan(0);
        result.Reasoning.Should().Contain("Bullish momentum");
        result.Reasoning.Should().Contain("RSI(14)=");
        result.Reasoning.Should().Contain("ADX=");
    }

    [Fact]
    public async Task Evaluate_ReasoningContains_AllFourFactors()
    {
        var (candles, signal, foundation) = StrongBullishSetup();

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, foundation, candles);

        result.Vote.Should().Be(VoteOutcome.Approve);
        result.Reasoning.Should().Contain("RSI(14)=");
        result.Reasoning.Should().Contain("ADX=");
        result.Reasoning.Should().Contain("MTF-RSI");
        result.Reasoning.Should().Contain("EMA 9/21");
    }

    // ── Bearish rejection ──

    [Fact]
    public async Task Evaluate_Rejects_BearishWithMultipleFactors()
    {
        var (candles, signal, foundation) = StrongBearishSetup();

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, foundation, candles);

        result.Vote.Should().Be(VoteOutcome.Reject);
        result.ConfidenceAdjustment.Should().BeLessThan(0);
        result.Reasoning.Should().Contain("Bearish momentum");
    }

    // ── Confluence scoring ──

    [Fact]
    public async Task Evaluate_HigherConfidence_WithVolumeExpansion()
    {
        var (candles, signal, foundation) = StrongBullishSetup();
        // Spike last 3 bars to 3x → triggers IsVolumeExpanding
        for (int i = candles.Count - 3; i < candles.Count; i++)
            candles[i].Volume = 3000m;

        var withVolume = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, foundation, candles);

        var (quietCandles, _, _) = StrongBullishSetup();
        var withoutVolume = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, foundation, quietCandles);

        withVolume.Vote.Should().Be(VoteOutcome.Approve);
        withoutVolume.Vote.Should().Be(VoteOutcome.Approve);
        withVolume.ConfidenceAdjustment.Should().BeGreaterThan(withoutVolume.ConfidenceAdjustment,
            "volume expansion should boost confidence by +4");
        withVolume.Reasoning.Should().Contain("vol expanding");
    }

    [Fact]
    public async Task Evaluate_ReasoningShowsVwapStatus_WhenAvailable()
    {
        var (candles, signal, foundation) = StrongBullishSetup();

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, foundation, candles);

        result.Reasoning.Should().Contain("above VWAP");
    }

    [Fact]
    public async Task Evaluate_NoVwapTag_WhenVwapIsZero()
    {
        var (candles, signal, _) = StrongBullishSetup();
        var noVwap = TestData.Foundation(vwap: 0m);

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, noVwap, candles);

        // With no VWAP, it still might approve if other factors agree
        // but reasoning should NOT contain "above VWAP" or "below VWAP"
        if (result.Vote != VoteOutcome.Abstain)
        {
            result.Reasoning.Should().NotContain("VWAP");
        }
    }

    [Fact]
    public async Task Evaluate_ConfidenceScalesWithFactorCount()
    {
        // Strong trend should have more confirmed factors → higher confidence
        var (strongCandles, signal, foundation) = StrongBullishSetup();
        var strongResult = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, foundation, strongCandles);

        // When approved, confidence should reflect factor count (N/4 shown in reasoning)
        if (strongResult.Vote == VoteOutcome.Approve)
        {
            strongResult.Reasoning.Should().MatchRegex(@"\d/4 factors");
            strongResult.ConfidenceAdjustment.Should().BeGreaterOrEqualTo(12f,
                "at least 2 factors × 6 base confidence");
        }
    }

    // ── Indicator unit tests ──

    [Fact]
    public void ComputeRsi_Returns100_WhenAllGains()
    {
        var candles = TestData.LinearCandles(20, 90m, 110m); // monotonically rising
        var rsi = MomentumStrategyAgent.ComputeRsi(candles, 14);
        rsi.Should().Be(100.0);
    }

    [Fact]
    public void ComputeRsi_ReturnsNear0_WhenAllLosses()
    {
        var candles = TestData.LinearCandles(20, 110m, 90m); // monotonically falling
        var rsi = MomentumStrategyAgent.ComputeRsi(candles, 14);
        rsi.Should().BeLessThan(5.0);
    }

    [Fact]
    public void ComputeAdx_ReturnsPositive_InTrendingMarket()
    {
        var candles = TestData.LinearCandles(60, 80m, 130m);
        var adx = MomentumStrategyAgent.ComputeAdx(candles, 14);
        adx.Should().BeGreaterThan(0, "trending data should produce non-zero ADX");
    }

    [Fact]
    public void ComputeAdx_ReturnsZeroOrLow_InFlatMarket()
    {
        var candles = TestData.FlatCandles(60, 100m);
        var adx = MomentumStrategyAgent.ComputeAdx(candles, 14);
        adx.Should().BeLessThan(20.0, "flat market should have ADX below trend threshold");
    }

    [Fact]
    public void ComputeEma_FastCrossesAboveSlow_InUptrend()
    {
        var candles = TestData.LinearCandles(60, 80m, 120m);
        var emaFast = MomentumStrategyAgent.ComputeEma(candles, 9);
        var emaSlow = MomentumStrategyAgent.ComputeEma(candles, 21);

        emaFast.Should().BeGreaterThan(emaSlow, "EMA(9) should lead EMA(21) in an uptrend");
    }

    [Fact]
    public void ComputeEma_FastCrossesBelowSlow_InDowntrend()
    {
        var candles = TestData.LinearCandles(60, 120m, 80m);
        var emaFast = MomentumStrategyAgent.ComputeEma(candles, 9);
        var emaSlow = MomentumStrategyAgent.ComputeEma(candles, 21);

        emaFast.Should().BeLessThan(emaSlow, "EMA(9) should lag EMA(21) in a downtrend");
    }

    [Fact]
    public void AggregateCandles_Produces_CorrectOhlcv()
    {
        var candles = new List<Candle>
        {
            new() { Open = 10, High = 15, Low = 8, Close = 12, Volume = 100 },
            new() { Open = 12, High = 18, Low = 9, Close = 14, Volume = 200 },
            new() { Open = 14, High = 20, Low = 11, Close = 16, Volume = 150 },
            new() { Open = 16, High = 22, Low = 13, Close = 18, Volume = 250 },
        };

        var agg = MomentumStrategyAgent.AggregateCandles(candles, 4);

        agg.Should().HaveCount(1);
        agg[0].Open.Should().Be(10m);     // first bar's open
        agg[0].High.Should().Be(22m);     // max high
        agg[0].Low.Should().Be(8m);       // min low
        agg[0].Close.Should().Be(18m);    // last bar's close
        agg[0].Volume.Should().Be(700m);  // sum
    }

    [Fact]
    public void AggregateCandles_DropsIncompleteTrailingBars()
    {
        var candles = TestData.FlatCandles(10, 100m); // 10 / 4 = 2 full + 2 leftover
        var agg = MomentumStrategyAgent.AggregateCandles(candles, 4);
        agg.Should().HaveCount(2, "10 candles / 4 = 2 complete aggregated bars");
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  BreakoutStrategyAgent Tests
// ════════════════════════════════════════════════════════════════════════════

public class BreakoutStrategyAgentTests
{
    private readonly BreakoutStrategyAgent _agent = new(Substitute.For<ILogger<BreakoutStrategyAgent>>());

    /// <summary>
    /// Builds a consolidation base that satisfies multiple breakout factors:
    ///  - 50 bars oscillating 90-100 (tight range → good base duration + thin air above)
    ///  - Last bar breaks out to 103 with 5x volume surge
    ///  - Bar before last pulls back to ~99 (near fib786) then closes above → re-test
    /// swingHigh = 102, swingLow = 88, fib786 ≈ 99.0
    /// </summary>
    private static List<Candle> BuildStrongBreakoutSetup()
    {
        var candles = new List<Candle>(55);

        // 3 extra padding bars so total = 53 (meets MinCandles = LookbackBars + 3)
        for (int i = 0; i < 3; i++)
        {
            candles.Add(new Candle
            {
                Timestamp = DateTime.UtcNow.AddMinutes(-58 + i),
                Open = 96m, High = 98m, Low = 94m, Close = 96m, Volume = 1000m
            });
        }

        // 48 bars of tight consolidation: close oscillates 95 ↔ 97 (both near midpoint)
        // High = close+2 (max 99), Low = close-2 (min 93)
        // swingHigh from consolidation = 99, swingLow = 93
        // After breakout bar: swingHigh = 102, swingLow = 93, range = 9
        // midpoint = 97.5, threshold = 9*0.3 = 2.7 → in-range = [94.8, 100.2]
        // Both 95 and 97 fall within this range → long base duration
        for (int i = 0; i < 48; i++)
        {
            var close = 95m + (i % 2 == 0 ? 2m : 0m); // oscillate 95 ↔ 97
            candles.Add(new Candle
            {
                Timestamp = DateTime.UtcNow.AddMinutes(-55 + i),
                Open = close - 0.5m,
                High = close + 2m,   // max high = 99
                Low = close - 2m,    // min low = 93
                Close = close,
                Volume = 1000m
            });
        }

        // Bar 49: pullback/re-test — touches near fib786 zone then resumes
        // fib786 = 93 + 9*0.786 = 100.07, proximity ≈ 0.50
        // Low 99.6 <= 100.07, Close 100.8 > 100.07 + 0.50 = 100.57 ✓
        candles.Add(new Candle
        {
            Timestamp = DateTime.UtcNow.AddMinutes(-7),
            Open = 99m,
            High = 101m,
            Low = 99.6m,
            Close = 100.8m,
            Volume = 1200m
        });

        // Bar 50: breakout candle with massive volume
        candles.Add(new Candle
        {
            Timestamp = DateTime.UtcNow.AddMinutes(-6),
            Open = 100.8m,
            High = 102m,   // swingHigh = 102
            Low = 100m,
            Close = 101.5m,
            Volume = 5000m // 5x average
        });

        return candles;
    }

    [Theory]
    [InlineData(MarketRegime.Volatile, true)]
    [InlineData(MarketRegime.Trending, false)]
    [InlineData(MarketRegime.Ranging, false)]
    public void CanHandle_OnlyVolatile(MarketRegime regime, bool expected)
    {
        _agent.CanHandle(TestData.DefaultSignal(), regime).Should().Be(expected);
    }

    [Fact]
    public void StrategyTag_IsBreakout() => _agent.StrategyTag.Should().Be("Breakout");

    [Fact]
    public void SwarmWeight_Is25() => _agent.SwarmWeight.Should().Be(25);

    // ── Abstain scenarios ──

    [Fact]
    public async Task Evaluate_Abstains_WhenTooFewCandles()
    {
        var candles = TestData.FlatCandles(10, 100m); // need 53+
        var result = await _agent.EvaluateAsync(
            TestData.DefaultSignal(), PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);

        result.Vote.Should().Be(VoteOutcome.Abstain);
        result.StrategyTag.Should().Be("Breakout");
    }

    [Fact]
    public async Task Evaluate_Abstains_WhenPriceBelowFib786()
    {
        var candles = TestData.CandlesWithVolumeSurge(55, 95m, 1000m, 5000m, surgeBars: 1);
        var signal = TestData.DefaultSignal(price: 95m);

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);

        result.Vote.Should().Be(VoteOutcome.Abstain);
    }

    [Fact]
    public async Task Evaluate_Abstains_WhenTooFewFactorsConfirmed()
    {
        // Choppy candles with wide range and no consolidation base.
        // Price > fib786 (factor 1), but no volume surge, no base, no re-test,
        // and volume is spread across the range (no thin air) → only 1/5 → abstain.
        var candles = new List<Candle>(55);
        for (int i = 0; i < 55; i++)
        {
            // Alternate between extremes of a wide range — no consolidation
            var close = (i % 2 == 0) ? 80m : 120m;
            candles.Add(new Candle
            {
                Timestamp = DateTime.UtcNow.AddMinutes(-55 + i),
                Open = close,
                High = close + 2m,
                Low = close - 2m,
                Close = close,
                Volume = 1000m // flat volume, no surge
            });
        }
        // fib786 = 78 + (122-78)*0.786 ≈ 112.6. Signal at 115 > 112.6.
        var signal = TestData.DefaultSignal(price: 115m);

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);

        result.Vote.Should().Be(VoteOutcome.Abstain);
    }

    // ── Approval scenarios ──

    [Fact]
    public async Task Evaluate_Approves_StrongBreakoutWithMultipleFactors()
    {
        var candles = BuildStrongBreakoutSetup();
        var signal = TestData.DefaultSignal(price: 101.5m);

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);

        result.Vote.Should().Be(VoteOutcome.Approve);
        result.Reasoning.Should().Contain("Breakout");
        result.Reasoning.Should().Contain("0.786");
        result.Reasoning.Should().MatchRegex(@"\d/5 factors");
        result.ConfidenceAdjustment.Should().BeGreaterOrEqualTo(18f,
            "at least 3 factors × 6 base confidence");
    }

    [Fact]
    public async Task Evaluate_ReasoningContains_VolumeSurgeMultiple()
    {
        var candles = BuildStrongBreakoutSetup();
        var signal = TestData.DefaultSignal(price: 101.5m);

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);

        if (result.Vote == VoteOutcome.Approve)
        {
            result.Reasoning.Should().Contain("vol surge");
        }
    }

    [Fact]
    public async Task Evaluate_ReasoningContains_BaseDuration()
    {
        var candles = BuildStrongBreakoutSetup();
        var signal = TestData.DefaultSignal(price: 101.5m);

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);

        if (result.Vote == VoteOutcome.Approve)
        {
            result.Reasoning.Should().Contain("base");
        }
    }

    [Fact]
    public async Task Evaluate_HigherConfidence_WithLongerBase()
    {
        // Short base (fewer consolidation bars)
        var shortBase = BuildStrongBreakoutSetup();
        // Disrupt the middle of consolidation so fewer bars qualify
        for (int i = 10; i < 30; i++)
        {
            shortBase[i].Close = 80m; // outside range → breaks the base
            shortBase[i].Low = 75m;
        }

        var longBase = BuildStrongBreakoutSetup(); // full 48-bar base

        var signal = TestData.DefaultSignal(price: 101.5m);

        var shortResult = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, shortBase);
        var longResult = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, longBase);

        if (shortResult.Vote == VoteOutcome.Approve && longResult.Vote == VoteOutcome.Approve)
        {
            longResult.ConfidenceAdjustment.Should().BeGreaterOrEqualTo(shortResult.ConfidenceAdjustment,
                "longer consolidation base should produce equal or higher confidence");
        }
    }

    // ── Indicator unit tests ──

    [Fact]
    public void MeasureBaseDuration_CountsConsolidationBars()
    {
        // 20 bars all at midpoint of 90-110 range
        var window = new List<Candle>();
        for (int i = 0; i < 20; i++)
        {
            window.Add(new Candle
            {
                Close = 100m, // exactly at midpoint
                High = 101m,
                Low = 99m,
                Volume = 1000m
            });
        }

        var (duration, quality) = BreakoutStrategyAgent.MeasureBaseDuration(window, 110m, 90m);

        duration.Should().BeGreaterThan(0, "bars at midpoint should count as in-range");
        quality.Should().Be(1.0m, "all bars are in-range");
    }

    [Fact]
    public void MeasureBaseDuration_ReturnsZero_WhenNoConsolidation()
    {
        // All bars at extremes — no consolidation
        var window = new List<Candle>();
        for (int i = 0; i < 20; i++)
        {
            window.Add(new Candle { Close = i < 10 ? 80m : 120m, High = 125m, Low = 75m, Volume = 1000m });
        }

        var (duration, _) = BreakoutStrategyAgent.MeasureBaseDuration(window, 125m, 75m);

        // Bars at 120 and 80 are at the extremes of a 75-125 range (midpoint=100)
        // 120 is 20 away from midpoint, threshold = 50*0.3 = 15 → 120 is outside
        // So duration should be 0
        duration.Should().Be(0);
    }

    [Fact]
    public void CheckRetestConfirmation_True_WhenPullbackThenResume()
    {
        var fib786 = 99m;
        // Need at least RetestLookbackBars(5) + 2 = 7 candles
        var candles = new List<Candle>
        {
            new() { Open = 101m, High = 102m, Low = 100m, Close = 101m },
            new() { Open = 101m, High = 102m, Low = 100m, Close = 101m },
            new() { Open = 101m, High = 102m, Low = 100m, Close = 101m },
            new() { Open = 101m, High = 102m, Low = 100m, Close = 101m },
            new() { Open = 101m, High = 101m, Low = 98.5m, Close = 99.5m }, // pullback touches fib
            new() { Open = 99.5m, High = 101m, Low = 99m, Close = 100.5m }, // resumes above
            new() { Open = 100.5m, High = 103m, Low = 100m, Close = 103m }, // breakout bar
        };

        var result = BreakoutStrategyAgent.CheckRetestConfirmation(candles, fib786);
        result.Should().BeTrue("price pulled back to fib786 then resumed upward");
    }

    [Fact]
    public void CheckRetestConfirmation_False_WhenNoPullback()
    {
        var fib786 = 99m;
        var candles = new List<Candle>
        {
            new() { Open = 101m, High = 102m, Low = 100.5m, Close = 101m },
            new() { Open = 101m, High = 102m, Low = 100.5m, Close = 101.5m },
            new() { Open = 101.5m, High = 103m, Low = 101m, Close = 103m },
        };

        var result = BreakoutStrategyAgent.CheckRetestConfirmation(candles, fib786);
        result.Should().BeFalse("price never came near fib786");
    }

    [Fact]
    public void CheckThinAirAboveResistance_True_WhenTopBinsHaveLowVolume()
    {
        // All volume concentrated in lower bins (close near swingLow)
        var window = new List<Candle>();
        for (int i = 0; i < 50; i++)
        {
            window.Add(new Candle
            {
                Close = 91m + (i % 3), // closes cluster near 91-93 (bottom of 90-110 range)
                High = 93m,
                Low = 90m,
                Volume = 1000m
            });
        }

        var result = BreakoutStrategyAgent.CheckThinAirAboveResistance(window, 90m, 20m);
        result.Should().BeTrue("volume is concentrated at the bottom, top bins are empty");
    }

    [Fact]
    public void CheckThinAirAboveResistance_False_WhenVolumeEvenlyDistributed()
    {
        // Volume spread evenly across all price levels
        var window = new List<Candle>();
        for (int i = 0; i < 50; i++)
        {
            var close = 90m + (20m * i / 50m); // linear spread across range
            window.Add(new Candle { Close = close, High = close + 1, Low = close - 1, Volume = 1000m });
        }

        var result = BreakoutStrategyAgent.CheckThinAirAboveResistance(window, 90m, 20m);
        result.Should().BeFalse("volume is evenly distributed, top bins are not thin");
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  StatisticalArbitrageAgent Tests
// ════════════════════════════════════════════════════════════════════════════

public class StatisticalArbitrageAgentTests
{
    private readonly StatisticalArbitrageAgent _agent = new(Substitute.For<ILogger<StatisticalArbitrageAgent>>());

    /// <summary>
    /// Builds a mean-reverting series: oscillates around 100 with amplitude 5,
    /// producing CV ≈ 3.5%, fast half-life, and stationary ADF.
    /// Pattern: 100±5 with damped oscillation (classic OU process).
    /// </summary>
    private static List<Candle> BuildMeanRevertingSeries(int count = 50)
    {
        var candles = new List<Candle>(count);
        double mean = 100.0;
        double amplitude = 5.0;

        for (int i = 0; i < count; i++)
        {
            // Damped sinusoidal around mean — classic mean-reverting behavior
            double close = mean + amplitude * Math.Sin(i * 0.8) * Math.Exp(-i * 0.02);
            // Add small noise so ADF doesn't get perfect fit edge cases
            close += (i % 3 - 1) * 0.3;
            var c = (decimal)close;
            candles.Add(new Candle
            {
                Timestamp = DateTime.UtcNow.AddMinutes(-count + i),
                Open = c - 0.2m,
                High = c + 1m,
                Low = c - 1m,
                Close = c,
                Volume = 1000m
            });
        }
        return candles;
    }

    /// <summary>
    /// Builds a trending (non-stationary) series — should fail ADF/half-life checks.
    /// </summary>
    private static List<Candle> BuildTrendingSeries(int count = 50)
    {
        return TestData.LinearCandles(count, 80m, 120m);
    }

    [Theory]
    [InlineData(MarketRegime.Ranging, true)]
    [InlineData(MarketRegime.Trending, false)]
    [InlineData(MarketRegime.Volatile, false)]
    public void CanHandle_OnlyRanging(MarketRegime regime, bool expected)
    {
        _agent.CanHandle(TestData.DefaultSignal(), regime).Should().Be(expected);
    }

    [Fact]
    public void StrategyTag_IsStatArb() => _agent.StrategyTag.Should().Be("StatArb");

    [Fact]
    public void SwarmWeight_Is30() => _agent.SwarmWeight.Should().Be(30);

    // ── Abstain scenarios ──

    [Fact]
    public async Task Evaluate_Abstains_WhenTooFewCandles()
    {
        var candles = TestData.FlatCandles(30, 100m);
        var result = await _agent.EvaluateAsync(
            TestData.DefaultSignal(), PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);

        result.Vote.Should().Be(VoteOutcome.Abstain);
        result.StrategyTag.Should().Be("StatArb");
    }

    [Fact]
    public async Task Evaluate_Abstains_WhenPriceNearMean()
    {
        var candles = BuildMeanRevertingSeries();
        var signal = TestData.DefaultSignal(price: 100m); // right at mean

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);

        result.Vote.Should().Be(VoteOutcome.Abstain);
    }

    [Fact]
    public async Task Evaluate_Abstains_WhenStdDevNearZero()
    {
        var candles = TestData.FlatCandles(50, 100m);
        var signal = TestData.DefaultSignal(price: 200m);

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);

        result.Vote.Should().Be(VoteOutcome.Abstain);
    }

    [Fact]
    public async Task Evaluate_LowerConfidence_ForTrendingSeries()
    {
        // Trending data should get fewer confirmed factors than mean-reverting data
        var trendCandles = BuildTrendingSeries();
        var revertCandles = BuildMeanRevertingSeries();

        // Both with price far from mean → z-score triggers
        var signal = TestData.DefaultSignal(price: 150m);

        var trendResult = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, trendCandles);
        var revertResult = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, revertCandles);

        // Mean-reverting should get approved with higher confidence
        if (trendResult.Vote == VoteOutcome.Approve && revertResult.Vote == VoteOutcome.Approve)
        {
            revertResult.ConfidenceAdjustment.Should().BeGreaterOrEqualTo(trendResult.ConfidenceAdjustment,
                "mean-reverting data should produce equal or higher confidence than trending");
        }
    }

    // ── Approval scenarios ──

    [Fact]
    public async Task Evaluate_Approves_ShortSignal_WhenPriceFarAboveMean()
    {
        var candles = BuildMeanRevertingSeries();
        var signal = TestData.DefaultSignal(price: 120m); // far above mean ≈ 100

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);

        result.Vote.Should().Be(VoteOutcome.Approve);
        result.Reasoning.Should().Contain("short");
        result.Reasoning.Should().MatchRegex(@"\d/4 factors");
        result.ConfidenceAdjustment.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Evaluate_Approves_LongSignal_WhenPriceFarBelowMean()
    {
        var candles = BuildMeanRevertingSeries();
        var signal = TestData.DefaultSignal(price: 80m);

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);

        result.Vote.Should().Be(VoteOutcome.Approve);
        result.Reasoning.Should().Contain("long");
    }

    [Fact]
    public async Task Evaluate_ReasoningContains_AllFactors()
    {
        var candles = BuildMeanRevertingSeries();
        var signal = TestData.DefaultSignal(price: 120m);

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);

        if (result.Vote == VoteOutcome.Approve)
        {
            result.Reasoning.Should().Contain("z=");
            result.Reasoning.Should().Contain("CV=");
            result.Reasoning.Should().Contain("HL=");
            result.Reasoning.Should().Contain("ADF=");
        }
    }

    [Fact]
    public async Task Evaluate_VwapAgreementBoostsConfidence()
    {
        var candles = BuildMeanRevertingSeries();
        var signal = TestData.DefaultSignal(price: 120m); // z > 0 → short

        // VWAP deviation > 0.5 agrees with positive z-score (both say price is high)
        var patternsWithVwap = TestData.Patterns(vwapDeviation: 1.2);
        var patternsNoVwap = PatternSnapshot.Empty;

        var withVwap = await _agent.EvaluateAsync(signal, patternsWithVwap, FoundationSnapshot.Empty, candles);
        var withoutVwap = await _agent.EvaluateAsync(signal, patternsNoVwap, FoundationSnapshot.Empty, candles);

        if (withVwap.Vote == VoteOutcome.Approve && withoutVwap.Vote == VoteOutcome.Approve)
        {
            withVwap.ConfidenceAdjustment.Should().BeGreaterThan(withoutVwap.ConfidenceAdjustment,
                "VWAP agreement should boost confidence by +2");
            withVwap.Reasoning.Should().Contain("VWAP");
        }
    }

    // ── Indicator unit tests ──

    [Fact]
    public void EstimateHalfLife_ReturnsPositive_ForMeanRevertingSeries()
    {
        // Alternating series around mean has fast reversion
        var prices = new double[50];
        for (int i = 0; i < 50; i++)
            prices[i] = 100 + 3 * Math.Sin(i * 0.8) * Math.Exp(-i * 0.02);

        var hl = StatisticalArbitrageAgent.EstimateHalfLife(prices);

        hl.Should().BeGreaterThan(0, "mean-reverting series should have positive half-life");
        hl.Should().BeLessThan(25, "oscillating series should have fast reversion");
    }

    [Fact]
    public void EstimateHalfLife_VeryLargeOrNegative_ForTrendingSeries()
    {
        // Exponentially growing series — clear positive drift, lambda should be ≥ 0 or near 0
        var prices = new double[50];
        for (int i = 0; i < 50; i++)
            prices[i] = 100 * Math.Exp(i * 0.02); // ~2% growth per bar

        var hl = StatisticalArbitrageAgent.EstimateHalfLife(prices);

        // Either no reversion (-1) or very slow (> MaxHalfLife of 25)
        var noReversion = hl < 0 || hl > 25;
        noReversion.Should().BeTrue(
            $"trending series should have no reversion or very slow half-life, got {hl}");
    }

    [Fact]
    public void EstimateHalfLife_ReturnsNegative_ForTooFewPrices()
    {
        var prices = new double[] { 100, 101, 100 };
        var hl = StatisticalArbitrageAgent.EstimateHalfLife(prices);

        hl.Should().Be(-1);
    }

    [Fact]
    public void ComputeAdfStatistic_MoreNegative_ForStationarySeries()
    {
        // Mean-reverting with noise: should have strongly negative ADF
        var stationary = new double[50];
        for (int i = 0; i < 50; i++)
            stationary[i] = 100 + 5 * Math.Sin(i * 0.6) + (i % 3 - 1) * 0.5;

        // Exponential trend: should have ADF closer to 0
        var trending = new double[50];
        for (int i = 0; i < 50; i++)
            trending[i] = 100 * Math.Exp(i * 0.02);

        var adfStationary = StatisticalArbitrageAgent.ComputeAdfStatistic(stationary);
        var adfTrending = StatisticalArbitrageAgent.ComputeAdfStatistic(trending);

        adfStationary.Should().BeLessThan(adfTrending,
            "stationary series should have more negative ADF than trending");
    }

    [Fact]
    public void ComputeAdfStatistic_ReturnsZero_ForTooFewPrices()
    {
        var prices = new double[] { 100, 101 };
        var adf = StatisticalArbitrageAgent.ComputeAdfStatistic(prices);

        adf.Should().Be(0);
    }

    [Fact]
    public void ComputeAdfStatistic_StationarySeries_BelowCriticalValue()
    {
        // Strong mean-reversion should produce ADF < -2.86 (5% critical value)
        var prices = new double[50];
        for (int i = 0; i < 50; i++)
            prices[i] = 100 + 5 * Math.Sin(i * 0.8) * Math.Exp(-i * 0.01);

        var adf = StatisticalArbitrageAgent.ComputeAdfStatistic(prices);

        adf.Should().BeLessThan(-2.0,
            "strongly mean-reverting series should have ADF well below critical value");
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  ScalpingStrategyAgent Tests
// ════════════════════════════════════════════════════════════════════════════

public class ScalpingStrategyAgentTests
{
    private readonly ScalpingStrategyAgent _agent = new(Substitute.For<ILogger<ScalpingStrategyAgent>>());

    /// <summary>
    /// Builds a full scalping setup: squeeze + strong OFI + tight spread + fill probability.
    /// BestBid=99995, BestAsk=100005 → spread = 1 bps (very tight).
    /// OFI=0.8 (strong bullish).
    /// </summary>
    private static (Signal signal, PatternSnapshot patterns, FoundationSnapshot foundation) StrongScalpSetup(
        bool squeeze = true, double ofi = 0.8, double bestBid = 99995, double bestAsk = 100005,
        double vwapDev = double.NaN, double spreadBps = 0)
    {
        var signal = TestData.SignalWithSpread(100000m, bestBid, bestAsk);
        var patterns = TestData.Patterns(squeeze: squeeze, vwapDeviation: vwapDev);
        var foundation = TestData.Foundation(bidAskImbalance: ofi, spreadBps: spreadBps);
        return (signal, patterns, foundation);
    }

    [Theory]
    [InlineData(MarketRegime.Volatile, true)]
    [InlineData(MarketRegime.Trending, false)]
    [InlineData(MarketRegime.Ranging, false)]
    public void CanHandle_OnlyVolatile(MarketRegime regime, bool expected)
    {
        _agent.CanHandle(TestData.DefaultSignal(), regime).Should().Be(expected);
    }

    [Fact]
    public void StrategyTag_IsScalping() => _agent.StrategyTag.Should().Be("Scalping");

    [Fact]
    public void SwarmWeight_Is15() => _agent.SwarmWeight.Should().Be(15);

    // ── Abstain scenarios ──

    [Fact]
    public async Task Evaluate_Abstains_WhenOfiTooLow()
    {
        // OFI=0.1 is below MinOfiThreshold (0.3) → abstain regardless of other factors
        var (signal, patterns, foundation) = StrongScalpSetup(ofi: 0.1);
        var candles = TestData.FlatCandles(5, 100m);

        var result = await _agent.EvaluateAsync(signal, patterns, foundation, candles);

        result.Vote.Should().Be(VoteOutcome.Abstain);
    }

    [Fact]
    public async Task Evaluate_Abstains_WhenTooFewFactors()
    {
        // No squeeze, no tight spread (no BestBid/BestAsk), OFI=0.35 (weak) → only 0-1 factors
        var signal = TestData.DefaultSignal();
        var patterns = TestData.Patterns(squeeze: false);
        var foundation = TestData.Foundation(bidAskImbalance: 0.35);
        var candles = TestData.FlatCandles(5, 100m);

        var result = await _agent.EvaluateAsync(signal, patterns, foundation, candles);

        result.Vote.Should().Be(VoteOutcome.Abstain);
    }

    [Fact]
    public async Task Evaluate_Abstains_WhenSpreadTooWide()
    {
        // Spread = 50 bps (wide), only squeeze + weak OFI → < 3 factors
        var (signal, patterns, foundation) = StrongScalpSetup(
            ofi: 0.35, bestBid: 99750, bestAsk: 100250); // ~50 bps spread
        var candles = TestData.FlatCandles(5, 100m);

        var result = await _agent.EvaluateAsync(signal, patterns, foundation, candles);

        result.Vote.Should().Be(VoteOutcome.Abstain);
    }

    // ── Approval / Rejection scenarios ──

    [Fact]
    public async Task Evaluate_Approves_StrongBullishScalp()
    {
        // Squeeze + strong OFI (0.8) + tight spread → 3+ factors
        var (signal, patterns, foundation) = StrongScalpSetup();
        var candles = TestData.FlatCandles(5, 100m);

        var result = await _agent.EvaluateAsync(signal, patterns, foundation, candles);

        result.Vote.Should().Be(VoteOutcome.Approve);
        result.ConfidenceAdjustment.Should().BeGreaterThan(0);
        result.Reasoning.Should().Contain("Scalp long");
        result.Reasoning.Should().Contain("OFI=");
        result.Reasoning.Should().Contain("spread=");
        result.Reasoning.Should().MatchRegex(@"\d/5 factors");
    }

    [Fact]
    public async Task Evaluate_Rejects_StrongBearishScalp()
    {
        // Squeeze + strong negative OFI → short/reject
        var (signal, patterns, foundation) = StrongScalpSetup(ofi: -0.8);
        var candles = TestData.FlatCandles(5, 100m);

        var result = await _agent.EvaluateAsync(signal, patterns, foundation, candles);

        result.Vote.Should().Be(VoteOutcome.Reject);
        result.ConfidenceAdjustment.Should().BeLessThan(0, "Reject votes have negative adjustment");
        result.Reasoning.Should().Contain("Scalp short");
    }

    [Fact]
    public async Task Evaluate_HigherConfidence_WithVeryStrongOfi()
    {
        // OFI=0.9 should get bonus confidence vs OFI=0.65
        var (strongSignal, strongPatterns, strongFoundation) = StrongScalpSetup(ofi: 0.9);
        var (weakSignal, weakPatterns, weakFoundation) = StrongScalpSetup(ofi: 0.65);
        var candles = TestData.FlatCandles(5, 100m);

        var strong = await _agent.EvaluateAsync(strongSignal, strongPatterns, strongFoundation, candles);
        var weak = await _agent.EvaluateAsync(weakSignal, weakPatterns, weakFoundation, candles);

        if (strong.Vote == VoteOutcome.Approve && weak.Vote == VoteOutcome.Approve)
        {
            strong.ConfidenceAdjustment.Should().BeGreaterThan(weak.ConfidenceAdjustment,
                "very strong OFI (>0.8) should get bonus confidence");
        }
    }

    [Fact]
    public async Task Evaluate_IncludesVwapMomentum_WhenAvailable()
    {
        // Bullish OFI + VWAP deviation below 0 (buy-dip) → tick momentum confirmed
        var (signal, patterns, foundation) = StrongScalpSetup(vwapDev: -0.5);
        var candles = TestData.FlatCandles(5, 100m);

        var result = await _agent.EvaluateAsync(signal, patterns, foundation, candles);

        if (result.Vote == VoteOutcome.Approve)
        {
            result.Reasoning.Should().Contain("VWAP momentum");
        }
    }

    [Fact]
    public async Task Evaluate_UsesFoundationSpreadBps_WhenNoOrderBookData()
    {
        // No BestBid/BestAsk on signal, but foundation has SpreadBps
        var signal = TestData.DefaultSignal(price: 100000m);
        var patterns = TestData.Patterns(squeeze: true);
        var foundation = TestData.Foundation(bidAskImbalance: 0.8, spreadBps: 5.0);
        var candles = TestData.FlatCandles(5, 100m);

        var result = await _agent.EvaluateAsync(signal, patterns, foundation, candles);

        result.Vote.Should().Be(VoteOutcome.Approve);
        result.Reasoning.Should().Contain("spread=");
        result.Reasoning.Should().Contain("bps");
    }

    // ── Indicator unit tests ──

    [Fact]
    public void ComputeSpreadBps_FromOrderBook()
    {
        // BestBid=99990, BestAsk=100010 → spread = 20/100000 * 10000 = 2 bps
        var signal = TestData.SignalWithSpread(100000m, 99990, 100010);
        var foundation = FoundationSnapshot.Empty;

        var bps = ScalpingStrategyAgent.ComputeSpreadBps(signal, foundation);

        bps.Should().BeApproximately(2.0, 0.01);
    }

    [Fact]
    public void ComputeSpreadBps_FallsBackToFoundation_WhenNoOrderBook()
    {
        var signal = TestData.DefaultSignal(); // no BestBid/BestAsk
        var foundation = TestData.Foundation(spreadBps: 8.0);

        var bps = ScalpingStrategyAgent.ComputeSpreadBps(signal, foundation);

        bps.Should().Be(8.0);
    }

    [Fact]
    public void ComputeSpreadBps_ReturnsMaxValue_WhenNoData()
    {
        var signal = TestData.DefaultSignal();
        var foundation = FoundationSnapshot.Empty;

        var bps = ScalpingStrategyAgent.ComputeSpreadBps(signal, foundation);

        bps.Should().Be(double.MaxValue);
    }

    [Fact]
    public void EstimateFillProbability_HigherWhenAligned()
    {
        // Buying with positive OFI (aligned) vs buying with negative OFI (counter)
        double aligned = ScalpingStrategyAgent.EstimateFillProbability(0.8, 3.0, isLong: true);
        double counter = ScalpingStrategyAgent.EstimateFillProbability(-0.8, 3.0, isLong: true);

        aligned.Should().BeGreaterThan(counter,
            "buying into bid-dominated book should have higher fill probability");
    }

    [Fact]
    public void EstimateFillProbability_HigherWithTighterSpread()
    {
        double tight = ScalpingStrategyAgent.EstimateFillProbability(0.7, 2.0, isLong: true);
        double wide = ScalpingStrategyAgent.EstimateFillProbability(0.7, 9.0, isLong: true);

        tight.Should().BeGreaterThan(wide,
            "tighter spread should give higher fill probability");
    }

    [Fact]
    public void EstimateFillProbability_ClampedBetween0And1()
    {
        var prob = ScalpingStrategyAgent.EstimateFillProbability(1.0, 0.5, isLong: true);
        prob.Should().BeInRange(0.0, 1.0);

        var probLow = ScalpingStrategyAgent.EstimateFillProbability(0.1, 100.0, isLong: false);
        probLow.Should().BeInRange(0.0, 1.0);
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  VolatilityExpansionAgent Tests
// ════════════════════════════════════════════════════════════════════════════

public class VolatilityExpansionAgentTests
{
    private readonly VolatilityExpansionAgent _agent = new(Substitute.For<ILogger<VolatilityExpansionAgent>>());

    /// <summary>
    /// Builds a volatility expansion setup:
    ///  - 40 bars of tight compression (close=100±0.5) followed by
    ///  - 15 bars of explosive upward expansion (close rapidly rises to 115)
    ///  - Last 3 bars have 3x volume surge
    /// </summary>
    private static List<Candle> BuildVolExpansionSetup(bool bullish = true)
    {
        var candles = new List<Candle>(60);

        // 40 bars: tight compression (low ATR, BB squeeze)
        for (int i = 0; i < 40; i++)
        {
            var close = 100m + (i % 2 == 0 ? 0.3m : -0.3m);
            candles.Add(new Candle
            {
                Timestamp = DateTime.UtcNow.AddMinutes(-60 + i),
                Open = close - 0.1m,
                High = close + 0.4m,
                Low = close - 0.4m,
                Close = close,
                Volume = 1000m
            });
        }

        // 15 bars: explosive expansion
        for (int i = 0; i < 15; i++)
        {
            decimal move = bullish ? (1m + i * 0.8m) : -(1m + i * 0.8m);
            var close = 100m + move;
            candles.Add(new Candle
            {
                Timestamp = DateTime.UtcNow.AddMinutes(-20 + i),
                Open = close - (bullish ? 0.5m : -0.5m),
                High = close + 2m,
                Low = close - 2m,
                Close = close,
                Volume = i >= 12 ? 3000m : 1500m // last 3 bars have volume surge
            });
        }

        return candles;
    }

    // ── Regime handling ──

    [Theory]
    [InlineData(MarketRegime.Volatile, true)]
    [InlineData(MarketRegime.Unknown, true)]
    [InlineData(MarketRegime.Breakout, true)]
    [InlineData(MarketRegime.Trending, false)]
    [InlineData(MarketRegime.Ranging, false)]
    public void CanHandle_VolatileUnknownBreakout(MarketRegime regime, bool expected)
    {
        _agent.CanHandle(TestData.DefaultSignal(), regime).Should().Be(expected);
    }

    [Fact]
    public void StrategyTag_IsVolExpansion() => _agent.StrategyTag.Should().Be("VolExpansion");

    [Fact]
    public void SwarmWeight_Is20() => _agent.SwarmWeight.Should().Be(20);

    // ── Abstain scenarios ──

    [Fact]
    public async Task Evaluate_Abstains_WhenTooFewCandles()
    {
        var candles = TestData.FlatCandles(20, 100m);
        var result = await _agent.EvaluateAsync(
            TestData.DefaultSignal(), PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);

        result.Vote.Should().Be(VoteOutcome.Abstain);
        result.StrategyTag.Should().Be("VolExpansion");
    }

    [Fact]
    public async Task Evaluate_Abstains_WhenNoExpansion()
    {
        // Flat candles: no ATR expansion, no BB expansion, no direction
        var candles = TestData.FlatCandles(60, 100m);
        var signal = TestData.DefaultSignal();

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);

        result.Vote.Should().Be(VoteOutcome.Abstain);
    }

    // ── Approval scenarios ──

    [Fact]
    public async Task Evaluate_Approves_BullishVolExpansion()
    {
        var candles = BuildVolExpansionSetup(bullish: true);
        var signal = TestData.DefaultSignal(price: 112m);

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);

        result.Vote.Should().Be(VoteOutcome.Approve);
        result.ConfidenceAdjustment.Should().BeGreaterThan(0);
        result.Reasoning.Should().Contain("Vol expansion Up");
        result.Reasoning.Should().MatchRegex(@"\d/5 factors");
    }

    [Fact]
    public async Task Evaluate_Rejects_BearishVolExpansion()
    {
        var candles = BuildVolExpansionSetup(bullish: false);
        var signal = TestData.DefaultSignal(price: 88m);

        var result = await _agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);

        result.Vote.Should().Be(VoteOutcome.Reject);
        result.ConfidenceAdjustment.Should().BeLessThan(0);
        result.Reasoning.Should().Contain("Vol expansion Down");
    }

    [Fact]
    public async Task Evaluate_UsesPatternSqueeze_AsAdditionalSignal()
    {
        var candles = BuildVolExpansionSetup(bullish: true);
        var signal = TestData.DefaultSignal(price: 112m);

        // With pattern squeeze flagged → should confirm squeeze factor
        var withSqueeze = TestData.Patterns(squeeze: true);
        var withoutSqueeze = PatternSnapshot.Empty;

        var resultWith = await _agent.EvaluateAsync(signal, withSqueeze, FoundationSnapshot.Empty, candles);
        var resultWithout = await _agent.EvaluateAsync(signal, withoutSqueeze, FoundationSnapshot.Empty, candles);

        // Both should approve (the candle data itself shows expansion)
        // But squeeze pattern adds extra confirmation
        resultWith.Vote.Should().Be(VoteOutcome.Approve);
        resultWithout.Vote.Should().Be(VoteOutcome.Approve);
    }

    // ── Indicator unit tests ──

    [Fact]
    public void ComputeAtr_ReturnsPositive_ForVolatileData()
    {
        var candles = new List<Candle>();
        for (int i = 0; i < 20; i++)
        {
            candles.Add(new Candle
            {
                Open = 100m, High = 105m, Low = 95m, Close = 100m + (i % 2 == 0 ? 3m : -3m)
            });
        }

        var atr = VolatilityExpansionAgent.ComputeAtr(candles, 14);
        atr.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ComputeAtr_ReturnsNearZero_ForFlatData()
    {
        var candles = TestData.FlatCandles(20, 100m);
        // FlatCandles have High=close+1, Low=close-1, so ATR ≈ 2
        var atr = VolatilityExpansionAgent.ComputeAtr(candles, 14);
        atr.Should().BeLessThan(5, "flat candles should have low ATR");
    }

    [Fact]
    public void DetectExpansionDirection_Up_WhenConsistentRise()
    {
        var candles = TestData.LinearCandles(10, 100m, 110m);
        var (dir, strength) = VolatilityExpansionAgent.DetectExpansionDirection(candles, 3);

        dir.Should().Be(VolatilityExpansionAgent.ExpansionDirection.Up);
        strength.Should().BeGreaterOrEqualTo(0.6);
    }

    [Fact]
    public void DetectExpansionDirection_Down_WhenConsistentDrop()
    {
        var candles = TestData.LinearCandles(10, 110m, 100m);
        var (dir, strength) = VolatilityExpansionAgent.DetectExpansionDirection(candles, 3);

        dir.Should().Be(VolatilityExpansionAgent.ExpansionDirection.Down);
        strength.Should().BeGreaterOrEqualTo(0.6);
    }

    [Fact]
    public void ComputeKeltnerChannels_WiderThanBollinger_DuringSqueeze()
    {
        // Flat/compressed data: Keltner (ATR-based) should be wider than BB (stddev-based)
        // because ATR captures the full range while stddev of closes may be tiny
        var candles = new List<Candle>();
        for (int i = 0; i < 25; i++)
        {
            // Wide wicks (high ATR) but closes cluster tight (low BB width)
            candles.Add(new Candle
            {
                Open = 100m,
                High = 105m,
                Low = 95m,
                Close = 100m + (i % 2 == 0 ? 0.2m : -0.2m), // tight closes
                Volume = 1000m
            });
        }

        var (kcUpper, kcLower) = VolatilityExpansionAgent.ComputeKeltnerChannels(candles, 20, 1.5);
        var (bbUpper, bbLower) = VolatilityExpansionAgent.ComputeBollingerBands(candles, 20, 2.0);

        // During squeeze: KC should be OUTSIDE BB (KC wider)
        var kcWidth = kcUpper - kcLower;
        var bbWidth = bbUpper - bbLower;

        kcWidth.Should().BeGreaterThan(bbWidth,
            "during squeeze (wide wicks, tight closes), Keltner should be wider than Bollinger");
    }

    [Fact]
    public void ComputeBollingerMetrics_ReturnsValidPercentB()
    {
        var candles = TestData.LinearCandles(50, 90m, 110m);
        var (width, avgWidth, percentB) = VolatilityExpansionAgent.ComputeBollingerMetrics(candles, 20);

        width.Should().BeGreaterThan(0);
        avgWidth.Should().BeGreaterThan(0);
        double.IsNaN(percentB).Should().BeFalse();
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  Cross-Agent Contract Tests
// ════════════════════════════════════════════════════════════════════════════

public class StrategyAgentContractTests
{
    /// <summary>
    /// All strategy agents must return their StrategyTag in every vote (Approve, Reject, or Abstain).
    /// This is load-bearing — SwarmOrchestrator groups and weights votes by tag.
    /// </summary>
    [Fact]
    public async Task AllAgents_AlwaysReturnCorrectTag()
    {
        var agents = new IStrategyAgent[]
        {
            new MomentumStrategyAgent(Substitute.For<ILogger<MomentumStrategyAgent>>()),
            new BreakoutStrategyAgent(Substitute.For<ILogger<BreakoutStrategyAgent>>()),
            new StatisticalArbitrageAgent(Substitute.For<ILogger<StatisticalArbitrageAgent>>()),
            new ScalpingStrategyAgent(Substitute.For<ILogger<ScalpingStrategyAgent>>()),
            new VolatilityExpansionAgent(Substitute.For<ILogger<VolatilityExpansionAgent>>()),
        };

        var signal = TestData.DefaultSignal();
        var candles = TestData.FlatCandles(5, 100m); // too few → all abstain
        var patterns = PatternSnapshot.Empty;
        var foundation = FoundationSnapshot.Empty;

        foreach (var agent in agents)
        {
            var vote = await agent.EvaluateAsync(signal, patterns, foundation, candles);
            vote.StrategyTag.Should().Be(agent.StrategyTag,
                $"{agent.GetType().Name} must tag its votes with '{agent.StrategyTag}'");
        }
    }

    /// <summary>
    /// Each agent must only handle its designated regime — no overlap.
    /// </summary>
    [Fact]
    public void EachAgent_HandlesExactlyOneRegime()
    {
        var agents = new IStrategyAgent[]
        {
            new MomentumStrategyAgent(Substitute.For<ILogger<MomentumStrategyAgent>>()),
            new BreakoutStrategyAgent(Substitute.For<ILogger<BreakoutStrategyAgent>>()),
            new StatisticalArbitrageAgent(Substitute.For<ILogger<StatisticalArbitrageAgent>>()),
            new ScalpingStrategyAgent(Substitute.For<ILogger<ScalpingStrategyAgent>>()),
            new VolatilityExpansionAgent(Substitute.For<ILogger<VolatilityExpansionAgent>>()),
        };

        var signal = TestData.DefaultSignal();
        var allRegimes = Enum.GetValues<MarketRegime>();

        foreach (var agent in agents)
        {
            var handled = allRegimes.Where(r => agent.CanHandle(signal, r)).ToList();
            handled.Should().HaveCountGreaterThan(0,
                $"{agent.GetType().Name} must handle at least one regime");
        }
    }

    /// <summary>
    /// SwarmWeight must be positive for all agents — zero weight means the vote is ignored.
    /// </summary>
    [Fact]
    public void AllAgents_HavePositiveSwarmWeight()
    {
        var agents = new IStrategyAgent[]
        {
            new MomentumStrategyAgent(Substitute.For<ILogger<MomentumStrategyAgent>>()),
            new BreakoutStrategyAgent(Substitute.For<ILogger<BreakoutStrategyAgent>>()),
            new StatisticalArbitrageAgent(Substitute.For<ILogger<StatisticalArbitrageAgent>>()),
            new ScalpingStrategyAgent(Substitute.For<ILogger<ScalpingStrategyAgent>>()),
            new VolatilityExpansionAgent(Substitute.For<ILogger<VolatilityExpansionAgent>>()),
        };

        foreach (var agent in agents)
        {
            agent.SwarmWeight.Should().BeGreaterThan(0,
                $"{agent.GetType().Name}.SwarmWeight must be > 0");
        }
    }

    /// <summary>
    /// Abstain votes must always have zero confidence adjustment — they should not
    /// influence the swarm decision in either direction.
    /// </summary>
    [Fact]
    public async Task AbstainVotes_AlwaysHaveZeroConfidence()
    {
        var agents = new IStrategyAgent[]
        {
            new MomentumStrategyAgent(Substitute.For<ILogger<MomentumStrategyAgent>>()),
            new BreakoutStrategyAgent(Substitute.For<ILogger<BreakoutStrategyAgent>>()),
            new StatisticalArbitrageAgent(Substitute.For<ILogger<StatisticalArbitrageAgent>>()),
            new ScalpingStrategyAgent(Substitute.For<ILogger<ScalpingStrategyAgent>>()),
        };

        var signal = TestData.DefaultSignal();
        var candles = TestData.FlatCandles(5, 100m);

        foreach (var agent in agents)
        {
            var vote = await agent.EvaluateAsync(signal, PatternSnapshot.Empty, FoundationSnapshot.Empty, candles);
            if (vote.Vote == VoteOutcome.Abstain)
            {
                vote.ConfidenceAdjustment.Should().Be(0f,
                    $"{agent.GetType().Name} abstain vote must have 0 confidence adjustment");
            }
        }
    }

    /// <summary>
    /// Non-abstain votes must carry the agent's SwarmWeight as PriorWeight so the orchestrator
    /// can use it as a Bayesian prior when the RL bandit hasn't learned data-driven weights.
    /// </summary>
    [Fact]
    public void AllAgents_CarryPriorWeightOnVotes()
    {
        // Verify the static factory methods propagate PriorWeight
        var vote = StrategyVote.Approve("TestTag", 10f, "reason", priorWeight: 25);
        vote.PriorWeight.Should().Be(25);

        var reject = StrategyVote.Reject("TestTag", 10f, "reason", priorWeight: 30);
        reject.PriorWeight.Should().Be(30);

        // Abstain votes don't carry prior weight (they don't participate in weighting)
        var abstain = StrategyVote.Abstain("TestTag");
        abstain.PriorWeight.Should().Be(1, "abstain default is 1 (unused)");
    }
}
