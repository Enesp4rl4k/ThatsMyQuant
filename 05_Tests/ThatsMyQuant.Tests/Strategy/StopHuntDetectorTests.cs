using System;
using System.Collections.Generic;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class StopHuntDetectorTests
{
    [Fact]
    public void DetectOnLatestBar_NullCandles_Throws()
    {
        Action act = () => StopHuntDetector.DetectOnLatestBar(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DetectOnLatestBar_TooFewBars_ReturnsNull()
    {
        var candles = new List<Candle> { CreateCandle(DateTime.UtcNow, 100, 101, 99, 100, 1000) };
        var result = StopHuntDetector.DetectOnLatestBar(candles);
        result.Should().BeNull();
    }

    [Fact]
    public void DetectOnLatestBar_UpperHuntPattern_ReturnsSellFadeSignal()
    {
        // Build 80 normal bars at price 100, then a "hunt" bar that wicks above
        // the swing high then closes back into range with high volume.
        var candles = BuildBaselineCandles(numBars: 80, basePrice: 100m, baseVolume: 1000m, seed: 5);

        // Set the 60th bar's high to act as a clear swing high: 102.5.
        candles[60] = CreateCandle(candles[60].Timestamp, 100m, 102.5m, 99.5m, 100.5m, 1500m);

        // Hunt bar: wicks above 102.5 then closes back near 100.
        var huntBar = CreateCandle(
            candles[^1].Timestamp.AddHours(1),
            open: 100.5m, high: 103.0m, low: 100.2m, close: 100.6m, volume: 5000m);
        candles.Add(huntBar);

        var result = StopHuntDetector.DetectOnLatestBar(
            candles, lookbackBars: 50, wickAtrMultiple: 0.5);

        result.Should().NotBeNull();
        result!.HuntDirection.Should().Be(TradeSide.Buy, "wick swept buy-side stops above");
        result.FadeSide.Should().Be(TradeSide.Sell, "fade trade is opposite of hunt direction");
        result.LevelBreached.Should().BeApproximately(102.5m, 0.05m);
        result.StrengthScore.Should().BeGreaterThan(50);
    }

    [Fact]
    public void DetectOnLatestBar_LowerHuntPattern_ReturnsBuyFadeSignal()
    {
        var candles = BuildBaselineCandles(numBars: 80, basePrice: 100m, baseVolume: 1000m, seed: 7);

        // Plant a clear swing low at bar 60: low = 97.5.
        candles[60] = CreateCandle(candles[60].Timestamp, 100m, 100.5m, 97.5m, 99.5m, 1500m);

        // Hunt bar: wicks below 97.5, closes back above.
        var huntBar = CreateCandle(
            candles[^1].Timestamp.AddHours(1),
            open: 99.5m, high: 99.8m, low: 97.0m, close: 99.4m, volume: 5000m);
        candles.Add(huntBar);

        var result = StopHuntDetector.DetectOnLatestBar(
            candles, lookbackBars: 50, wickAtrMultiple: 0.5);

        result.Should().NotBeNull();
        result!.HuntDirection.Should().Be(TradeSide.Sell);
        result.FadeSide.Should().Be(TradeSide.Buy);
        result.LevelBreached.Should().BeApproximately(97.5m, 0.05m);
    }

    [Fact]
    public void DetectOnLatestBar_NoWick_ReturnsNull()
    {
        var candles = BuildBaselineCandles(numBars: 80, basePrice: 100m, baseVolume: 1000m, seed: 11);

        // A clean trending close (no wick) — no hunt.
        var cleanBar = CreateCandle(
            candles[^1].Timestamp.AddHours(1),
            open: 100m, high: 101m, low: 100m, close: 101m, volume: 1000m);
        candles.Add(cleanBar);

        var result = StopHuntDetector.DetectOnLatestBar(candles);
        result.Should().BeNull("no wick means no hunt");
    }

    [Fact]
    public void DetectOnLatestBar_LowVolume_ReturnsNull()
    {
        var candles = BuildBaselineCandles(numBars: 80, basePrice: 100m, baseVolume: 1000m, seed: 13);
        candles[60] = CreateCandle(candles[60].Timestamp, 100m, 102.5m, 99.5m, 100.5m, 1500m);

        // Hunt-shaped bar but with TINY volume (50, vs median 1000).
        var quietHunt = CreateCandle(
            candles[^1].Timestamp.AddHours(1),
            open: 100.5m, high: 103.0m, low: 100.2m, close: 100.6m, volume: 50m);
        candles.Add(quietHunt);

        var result = StopHuntDetector.DetectOnLatestBar(
            candles, volumeFloorMultiplier: 1.0m);
        result.Should().BeNull("hunt requires volume confirmation");
    }

    [Fact]
    public void DetectOnLatestBar_DoesNotSweepKeyLevel_ReturnsNull()
    {
        var candles = BuildBaselineCandles(numBars: 80, basePrice: 100m, baseVolume: 1000m, seed: 17);
        candles[60] = CreateCandle(candles[60].Timestamp, 100m, 105m, 99m, 100m, 1500m);  // swing high at 105

        // Wick reaches 102 — below the 105 swing high → no sweep.
        var partialBar = CreateCandle(
            candles[^1].Timestamp.AddHours(1),
            open: 100m, high: 102m, low: 99.8m, close: 100.2m, volume: 5000m);
        candles.Add(partialBar);

        var result = StopHuntDetector.DetectOnLatestBar(candles);
        result.Should().BeNull("wick must reach the swing extreme to count as a sweep");
    }

    [Fact]
    public void DetectOnLatestBar_StrongConfirmation_ScoresHigh()
    {
        var candles = BuildBaselineCandles(numBars: 80, basePrice: 100m, baseVolume: 1000m, seed: 19);
        candles[60] = CreateCandle(candles[60].Timestamp, 100m, 102.5m, 99.5m, 100.5m, 1500m);

        // Maximally strong hunt: large wick, decisive reverse-close, very high volume.
        var perfectHunt = CreateCandle(
            candles[^1].Timestamp.AddHours(1),
            open: 100.5m, high: 102.6m, low: 100m, close: 100.1m, volume: 10000m);
        candles.Add(perfectHunt);

        var result = StopHuntDetector.DetectOnLatestBar(candles, wickAtrMultiple: 0.3);
        result.Should().NotBeNull();
        result!.StrengthScore.Should().BeGreaterThan(70);
    }

    [Fact]
    public void DetectOnLatestBar_InvalidParameters_Throws()
    {
        var candles = BuildBaselineCandles(numBars: 80, basePrice: 100m, baseVolume: 1000m, seed: 23);
        Action wickMult = () => StopHuntDetector.DetectOnLatestBar(candles, wickAtrMultiple: 0);
        wickMult.Should().Throw<ArgumentOutOfRangeException>();
        Action atrPeriod = () => StopHuntDetector.DetectOnLatestBar(candles, atrPeriod: 1);
        atrPeriod.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static List<Candle> BuildBaselineCandles(int numBars, decimal basePrice, decimal baseVolume, int seed)
    {
        // Non-compounding (mean-reverting) candles: each bar's open/close stays near
        // basePrice. This keeps the planted swing high/low at index 60 the actual
        // extreme of the lookback window, so the hunt-detection tests are
        // deterministic regardless of seed.
        var rng = new Random(seed);
        var candles = new List<Candle>(numBars);
        var ts = DateTime.UtcNow.AddHours(-numBars);
        for (int i = 0; i < numBars; i++)
        {
            decimal openJitter = (decimal)(rng.NextDouble() - 0.5) * 0.4m;
            decimal closeJitter = (decimal)(rng.NextDouble() - 0.5) * 0.4m;
            decimal open = basePrice + openJitter;
            decimal close = basePrice + closeJitter;
            decimal high = Math.Max(open, close) + (decimal)rng.NextDouble() * 0.15m;
            decimal low = Math.Min(open, close) - (decimal)rng.NextDouble() * 0.15m;
            candles.Add(CreateCandle(ts.AddHours(i), open, high, low, close, baseVolume + (decimal)rng.NextDouble() * 200));
        }
        return candles;
    }

    private static Candle CreateCandle(DateTime ts, decimal open, decimal high, decimal low, decimal close, decimal volume)
    {
        return new Candle
        {
            Timestamp = ts,
            Open = open,
            High = high,
            Low = low,
            Close = close,
            Volume = volume
        };
    }
}
