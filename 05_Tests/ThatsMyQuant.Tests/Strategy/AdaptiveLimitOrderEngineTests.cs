using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class AdaptiveLimitOrderEngineTests
{
    private readonly AdaptiveLimitOrderEngine _engine;

    public AdaptiveLimitOrderEngineTests()
    {
        _engine = new AdaptiveLimitOrderEngine(Substitute.For<ILogger<AdaptiveLimitOrderEngine>>());
    }

    [Fact]
    public void ComputeOptimalPlacement_NarrowSpread_AlignedMomentum_FavorableDepth_SelectsPassiveMaker()
    {
        var signal = MakeSignal(TradeSide.Buy, 100m, 10m);
        var ctx = new AdaptiveLimitOrderEngine.MarketContext
        {
            BestBid = 99.98m,
            BestAsk = 100.02m,
            SpreadBps = 4m,           // Narrow
            BidDepth = 500m,
            AskDepth = 400m,          // AskDepth/BidDepth = 0.8 < 1.5 (favorable)
            MomentumSignal = 0.5f,    // Aligned with Buy
            Volatility = 0.01m,
            Volume24h = 5_000_000m,
            MakerFeeRate = 0.0002m,
            TakerFeeRate = 0.0004m,
        };

        var placement = _engine.ComputeOptimalPlacement(signal, ctx);

        placement.Type.Should().Be(AdaptiveLimitOrderEngine.OrderType.LimitMaker);
        placement.Price.Should().Be(99.98m); // Best bid for buy maker
        placement.Side.Should().Be(TradeSide.Buy);
        placement.TimeoutBeforeEscalation.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void ComputeOptimalPlacement_AdverseMomentum_HighVolatility_FallsBackToMarket()
    {
        var signal = MakeSignal(TradeSide.Buy, 100m, 5m);
        var ctx = new AdaptiveLimitOrderEngine.MarketContext
        {
            BestBid = 99.90m,
            BestAsk = 100.10m,
            SpreadBps = 20m,
            BidDepth = 300m,
            AskDepth = 600m,
            MomentumSignal = -0.5f,   // Against buy direction
            Volatility = 0.03m,       // High volatility
            Volume24h = 2_000_000m,
            MakerFeeRate = 0.0002m,
            TakerFeeRate = 0.0004m,
        };

        var placement = _engine.ComputeOptimalPlacement(signal, ctx);

        placement.Type.Should().Be(AdaptiveLimitOrderEngine.OrderType.MarketOrder);
        placement.Reasoning.Should().Contain("volatility");
    }

    [Fact]
    public void ComputeOptimalPlacement_MomentumAligned_DepthFavorable_WideSpread_SelectsAggressiveLimit()
    {
        var signal = MakeSignal(TradeSide.Sell, 100m, 8m);
        var ctx = new AdaptiveLimitOrderEngine.MarketContext
        {
            BestBid = 99.80m,
            BestAsk = 100.20m,
            SpreadBps = 40m,         // Wide spread — not narrow enough for passive
            BidDepth = 400m,
            AskDepth = 500m,
            MomentumSignal = -0.6f,  // Aligned with Sell
            Volatility = 0.015m,
            Volume24h = 3_000_000m,
            MakerFeeRate = 0.0002m,
            TakerFeeRate = 0.0004m,
        };

        var placement = _engine.ComputeOptimalPlacement(signal, ctx);

        placement.Type.Should().Be(AdaptiveLimitOrderEngine.OrderType.AggressiveLimit);
        placement.Side.Should().Be(TradeSide.Sell);
        // Aggressive limit for sell steps inside spread from ask side
        placement.Price.Should().BeLessThan(100.20m);
        placement.Price.Should().BeGreaterThan(99.80m);
    }

    [Fact]
    public void ComputeOptimalPlacement_InvalidOrderBook_FallsBackToMarket()
    {
        var signal = MakeSignal(TradeSide.Buy, 100m, 5m);
        var ctx = new AdaptiveLimitOrderEngine.MarketContext
        {
            BestBid = 0m,
            BestAsk = 0m,
        };

        var placement = _engine.ComputeOptimalPlacement(signal, ctx);

        placement.Type.Should().Be(AdaptiveLimitOrderEngine.OrderType.MarketOrder);
        placement.Reasoning.Should().Contain("No valid order book");
    }

    [Fact]
    public void ComputeOptimalPlacement_SellSide_PassiveMaker_PostsAtBestAsk()
    {
        var signal = MakeSignal(TradeSide.Sell, 100m, 10m);
        var ctx = new AdaptiveLimitOrderEngine.MarketContext
        {
            BestBid = 99.97m,
            BestAsk = 100.03m,
            SpreadBps = 5m,           // Narrow (<=5bps threshold)
            BidDepth = 400m,
            AskDepth = 500m,
            MomentumSignal = -0.5f,   // Aligned with Sell
            Volatility = 0.005m,
            Volume24h = 10_000_000m,
            MakerFeeRate = 0.0001m,
            TakerFeeRate = 0.0004m,   // Fee saving 3bps < spread 5bps = worth it
        };

        var placement = _engine.ComputeOptimalPlacement(signal, ctx);

        placement.Type.Should().Be(AdaptiveLimitOrderEngine.OrderType.LimitMaker);
        placement.Price.Should().Be(100.03m); // Best ask for sell maker
    }

    private static Signal MakeSignal(TradeSide side, decimal price, decimal qty) => new()
    {
        Symbol = "BTCUSDT",
        Side = side,
        Price = price,
        Quantity = qty,
        Timestamp = DateTime.UtcNow,
        Metadata = new Dictionary<string, string>()
    };
}
