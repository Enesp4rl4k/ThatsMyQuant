using FluentAssertions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class MultiTimeframeConfluenceServiceTests
{
    [Fact]
    public void Evaluate_WhenTimeframesAlign_ReturnsStrongScore()
    {
        var sut = new MultiTimeframeConfluenceService();
        var candles = new Dictionary<string, IReadOnlyList<Candle>>
        {
            ["5m"] = BuildSeries(100m, 104m, 5),
            ["1h"] = BuildSeries(100m, 108m, 6),
            ["1d"] = BuildSeries(100m, 115m, 7)
        };

        var result = sut.Evaluate(candles);

        result.Score.Should().BeGreaterThan(60);
        result.Label.Should().BeOneOf("Confirmed", "Strong");
    }

    [Fact]
    public void Evaluate_WhenTimeframesConflict_ReturnsWeakerScore()
    {
        var sut = new MultiTimeframeConfluenceService();
        var candles = new Dictionary<string, IReadOnlyList<Candle>>
        {
            ["5m"] = BuildSeries(100m, 104m, 5),
            ["1h"] = BuildSeries(104m, 101m, 6),
            ["1d"] = BuildSeries(100m, 99m, 7)
        };

        var result = sut.Evaluate(candles);

        result.Score.Should().BeLessThan(70);
    }

    private static IReadOnlyList<Candle> BuildSeries(decimal start, decimal end, int count)
    {
        var candles = new List<Candle>(count);
        var step = (end - start) / Math.Max(1, count - 1);
        for (var i = 0; i < count; i++)
        {
            var close = start + (step * i);
            candles.Add(new Candle
            {
                Timestamp = DateTime.UtcNow.AddMinutes(i),
                Open = close - 0.5m,
                High = close + 1m,
                Low = close - 1m,
                Close = close,
                Volume = 1000m + i
            });
        }

        return candles;
    }
}
