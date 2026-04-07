using FluentAssertions;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.MachineLearning;

public class ModelExperimentServiceTests
{
    [Fact]
    public void AssignVariant_IsDeterministicForSameSubject()
    {
        var sut = new ModelExperimentService();

        var first = sut.AssignVariant("exp-1", "BTCUSDT", ["model-b", "model-a"]);
        var second = sut.AssignVariant("exp-1", "BTCUSDT", ["model-a", "model-b"]);

        first.ModelVersion.Should().Be(second.ModelVersion);
        first.Bucket.Should().Be(second.Bucket);
    }

    [Fact]
    public void Evaluate_SelectsChampionUsingTradeOutcomes()
    {
        var sut = new ModelExperimentService();
        var signals = new[]
        {
            MakeSignal("model-a"),
            MakeSignal("model-a"),
            MakeSignal("model-b"),
            MakeSignal("model-b")
        };
        var trades = new[]
        {
            MakeTrade("model-a", 10m),
            MakeTrade("model-a", 8m),
            MakeTrade("model-b", -4m),
            MakeTrade("model-b", 1m)
        };

        var result = sut.Evaluate("exp-1", signals, trades);

        result.ChampionModelVersion.Should().Be("model-a");
        result.Snapshots.Should().HaveCount(2);
    }

    private static Signal MakeSignal(string modelVersion)
        => new()
        {
            Symbol = "BTCUSDT",
            Confidence = 80,
            Status = TradeStatus.Pending,
            Metadata = new Dictionary<string, string>
            {
                ["MlExperimentId"] = "exp-1",
                ["MlModelVersion"] = modelVersion
            }
        };

    private static Trade MakeTrade(string modelVersion, decimal pnl)
        => new()
        {
            Symbol = "BTCUSDT",
            MlModelVersion = modelVersion,
            EntryPrice = 100m,
            ExitPrice = 100m + pnl,
            Quantity = 1m,
            Side = TradeSide.Buy,
            Status = TradeStatus.Executed,
            ExitTime = DateTime.UtcNow
        };
}
