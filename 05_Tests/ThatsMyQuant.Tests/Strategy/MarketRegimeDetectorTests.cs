using System;
using System.Collections.Generic;
using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class MarketRegimeDetectorTests
{
    [Fact]
    public void Detect_TrendingMarket_ShouldIdentifyLowEntropy()
    {
        // Arrange
        // Very organized trending price action (low entropy, low noise)
        var candles = new List<Candle>();
        decimal price = 100m;
        for (int i = 0; i < 40; i++)
        {
            price += 0.5m; // Consistent upward movement
            candles.Add(new Candle { Close = price, High = price + 0.1m, Low = price - 0.1m });
        }

        // Act
        var result = MarketRegimeDetector.Detect(candles);

        // Assert
        result.Regime.Should().Be(MarketRegime.Trending);
        result.Description.Should().Contain("Organized structure");
    }

    [Fact]
    public void Detect_RangingMarket_ShouldIdentifyHighEntropy()
    {
        // Arrange
        // Very noisy ranging price action (high entropy)
        var candles = new List<Candle>();
        decimal basePrice = 100m;
        var random = new Random(42);
        for (int i = 0; i < 40; i++)
        {
            // Random walk noise
            decimal noise = (decimal)(random.NextDouble() * 2 - 1);
            candles.Add(new Candle { Close = basePrice + noise, High = basePrice + noise + 0.1m, Low = basePrice + noise - 0.1m });
        }

        // Act
        var result = MarketRegimeDetector.Detect(candles);

        // Assert
        result.Regime.Should().Be(MarketRegime.Ranging);
        result.Description.Should().Contain("High noise");
    }

    [Fact]
    public void Detect_GarchVolatilitySpike_ShouldIdentifyVolatileRegime()
    {
        // Test that GARCH catches volatility clusters before the moving average ATR does
        var candles = new List<Candle>();
        decimal price = 100m;
        
        // 40 Steady flat candles (ATR is tiny, enough to bypass lookback gates)
        for (int i = 0; i < 40; i++)
        {
            price += 0.1m;
            candles.Add(new Candle { Close = price, High = price + 0.05m, Low = price - 0.05m });
        }

        // 3 sudden 10% jumps/dumps. 
        // This causes extreme squared returns (GARCH spikes) but the 20-period Average ATR remains dampened by the 40 flat periods.
        price = 115m; 
        candles.Add(new Candle { Close = price, High = price, Low = price - 1m });
        price = 100m; 
        candles.Add(new Candle { Close = price, High = price + 1m, Low = price });
        price = 115m; 
        candles.Add(new Candle { Close = price, High = price, Low = price - 1m });

        var result = MarketRegimeDetector.Detect(candles);

        // Assert that we triggered Volatile strictly because of GARCH, despite low overall average ATR
        result.Regime.Should().Be(MarketRegime.Volatile);
        result.Description.Should().Contain("GARCH=");
    }
}
