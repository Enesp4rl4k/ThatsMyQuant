using System;
using System.Collections.Generic;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class FundingFlipDetectorTests
{
    [Fact]
    public void DetectLatestFlip_NullHistory_Throws()
    {
        Action act = () => FundingFlipDetector.DetectLatestFlip(null!, "BTC-USDT-SWAP");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DetectLatestFlip_EmptySymbol_Throws()
    {
        var history = new List<(DateTime, decimal)>();
        Action act = () => FundingFlipDetector.DetectLatestFlip(history, "");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DetectLatestFlip_FewerThanTwoBars_ReturnsNull()
    {
        var history = new List<(DateTime, decimal)>
        {
            (DateTime.UtcNow, 0.0001m)
        };
        var flip = FundingFlipDetector.DetectLatestFlip(history, "BTC-USDT-SWAP");
        flip.Should().BeNull();
    }

    [Fact]
    public void DetectLatestFlip_PositiveToNegative_ReturnsBuySignal()
    {
        // Funding regime: positive (longs paying), then flips negative.
        // → Crowded longs unwound → counter-trade is BUY (price expected to bounce up).
        var t = DateTime.UtcNow;
        var history = new List<(DateTime, decimal)>
        {
            (t.AddHours(-16), 0.0003m),
            (t.AddHours(-8),  0.0002m),
            (t,               -0.0001m)
        };
        var flip = FundingFlipDetector.DetectLatestFlip(history, "BTC-USDT-SWAP");

        flip.Should().NotBeNull();
        flip!.Side.Should().Be(TradeSide.Buy);
        flip.RateBefore.Should().Be(0.0002m);
        flip.RateAfter.Should().Be(-0.0001m);
        flip.MagnitudeBps.Should().BeApproximately(2.0, 0.01);
        flip.Symbol.Should().Be("BTC-USDT-SWAP");
    }

    [Fact]
    public void DetectLatestFlip_NegativeToPositive_ReturnsSellSignal()
    {
        // Funding regime: negative (shorts paying), then flips positive.
        // → Crowded shorts unwound → counter-trade is SELL.
        var t = DateTime.UtcNow;
        var history = new List<(DateTime, decimal)>
        {
            (t.AddHours(-16), -0.0003m),
            (t.AddHours(-8),  -0.0002m),
            (t,               0.0001m)
        };
        var flip = FundingFlipDetector.DetectLatestFlip(history, "BTC-USDT-SWAP");

        flip.Should().NotBeNull();
        flip!.Side.Should().Be(TradeSide.Sell);
        flip.MagnitudeBps.Should().BeApproximately(2.0, 0.01);
    }

    [Fact]
    public void DetectLatestFlip_BelowMinMagnitude_NoSignal()
    {
        // Tiny rates (below default 0.5 bps min) should not register as flips.
        var t = DateTime.UtcNow;
        var history = new List<(DateTime, decimal)>
        {
            (t.AddHours(-8),  0.00001m),   // 0.1 bp — below min
            (t,               -0.00001m)
        };
        var flip = FundingFlipDetector.DetectLatestFlip(history, "BTC-USDT-SWAP", minRateMagnitudeBps: 0.5m);
        flip.Should().BeNull();
    }

    [Fact]
    public void DetectLatestFlip_StaleFlip_RespectMaxAge()
    {
        // Flip happened 24h ago, but we only want flips within 4h of latest bar.
        var t = DateTime.UtcNow;
        var history = new List<(DateTime, decimal)>
        {
            (t.AddHours(-32), 0.0003m),
            (t.AddHours(-24), -0.0002m),  // ← flip here
            (t.AddHours(-16), -0.0002m),
            (t.AddHours(-8),  -0.00015m),
            (t,               -0.0001m)
        };
        var flip = FundingFlipDetector.DetectLatestFlip(
            history, "BTC-USDT-SWAP",
            maxAgeSinceFlip: TimeSpan.FromHours(4));
        flip.Should().BeNull("flip is 24h old, beyond the 4h staleness window");
    }

    [Fact]
    public void DetectLatestFlip_RecentFlipWithinMaxAge_ReturnsSignal()
    {
        var t = DateTime.UtcNow;
        var history = new List<(DateTime, decimal)>
        {
            (t.AddHours(-16), 0.0003m),
            (t.AddHours(-8),  0.0002m),
            (t,               -0.0001m)  // flip happened at the latest bar
        };
        var flip = FundingFlipDetector.DetectLatestFlip(
            history, "BTC-USDT-SWAP",
            maxAgeSinceFlip: TimeSpan.FromHours(4));
        flip.Should().NotBeNull();
    }

    [Fact]
    public void DetectLatestFlip_NoSignChange_ReturnsNull()
    {
        // All positive, decaying — no flip.
        var t = DateTime.UtcNow;
        var history = new List<(DateTime, decimal)>
        {
            (t.AddHours(-16), 0.0005m),
            (t.AddHours(-8),  0.0003m),
            (t,               0.0001m)
        };
        var flip = FundingFlipDetector.DetectLatestFlip(history, "BTC-USDT-SWAP");
        flip.Should().BeNull();
    }

    [Fact]
    public void DetectAllFlips_MultipleSignChanges_ReturnsAll()
    {
        // History oscillates between positive and negative regimes.
        var t = DateTime.UtcNow;
        var history = new List<(DateTime, decimal)>
        {
            (t.AddHours(-40), 0.0002m),
            (t.AddHours(-32), -0.00015m),  // flip 1: pos → neg
            (t.AddHours(-24), -0.0002m),
            (t.AddHours(-16), 0.00018m),   // flip 2: neg → pos
            (t.AddHours(-8),  0.0001m),
            (t,               -0.00012m)   // flip 3: pos → neg
        };
        var flips = FundingFlipDetector.DetectAllFlips(history, "BTC-USDT-SWAP");

        flips.Should().HaveCount(3);
        flips[0].Side.Should().Be(TradeSide.Buy);   // pos → neg
        flips[1].Side.Should().Be(TradeSide.Sell);  // neg → pos
        flips[2].Side.Should().Be(TradeSide.Buy);   // pos → neg
    }

    [Fact]
    public void DetectAllFlips_OldestToNewest()
    {
        var t = DateTime.UtcNow;
        var history = new List<(DateTime, decimal)>
        {
            (t.AddHours(-32), 0.0002m),
            (t.AddHours(-24), -0.00015m),
            (t.AddHours(-16), 0.00018m),
            (t.AddHours(-8),  -0.00012m),
            (t,               0.0001m)
        };
        var flips = FundingFlipDetector.DetectAllFlips(history, "BTC-USDT-SWAP");

        // Each timestamp must be later than the previous one.
        for (int i = 1; i < flips.Count; i++)
        {
            flips[i].FlipTimestamp.Should().BeAfter(flips[i - 1].FlipTimestamp);
        }
    }

    [Fact]
    public void DetectLatestFlip_DefaultExpectedHoldIsThreeHours()
    {
        var t = DateTime.UtcNow;
        var history = new List<(DateTime, decimal)>
        {
            (t.AddHours(-8), 0.0002m),
            (t,              -0.0001m)
        };
        var flip = FundingFlipDetector.DetectLatestFlip(history, "BTC-USDT-SWAP");
        flip!.ExpectedHoldDuration.Should().Be(TimeSpan.FromHours(3));
    }
}
