using FluentAssertions;
using System.Linq;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.MachineLearning;

public class ModelGovernanceServiceTests
{
    [Fact]
    public void Analyze_ShouldBuildPerModelPerformanceMetrics()
    {
        var service = new ModelGovernanceService();
        var signals = new[]
        {
            CreateSignal("model-a", DateTime.UtcNow.AddMinutes(-20), drift: true, veto: false),
            CreateSignal("model-a", DateTime.UtcNow.AddMinutes(-15), drift: true, veto: true),
            CreateSignal("model-a", DateTime.UtcNow.AddMinutes(-10), drift: false, veto: false),
            CreateSignal("model-b", DateTime.UtcNow.AddMinutes(-5), drift: false, veto: false)
        };
        var trades = new[]
        {
            CreateTrade("model-a", 12m, DateTime.UtcNow.AddMinutes(-18)),
            CreateTrade("model-a", -4m, DateTime.UtcNow.AddMinutes(-12)),
            CreateTrade("model-b", 8m, DateTime.UtcNow.AddMinutes(-4))
        };

        var snapshots = service.Analyze(signals, trades);

        snapshots.Should().HaveCount(2);
        snapshots.Should().Contain(snapshot =>
            snapshot.ModelVersion == "model-a" &&
            snapshot.SignalCount == 3 &&
            snapshot.ClosedTrades == 2 &&
            snapshot.DriftRate > 60 &&
            snapshot.VetoRate > 30 &&
            snapshot.SizeTrimRate > 30);
    }

    [Fact]
    public void EvaluateRetrainingNeed_ShouldTrigger_WhenDriftAndVetoStayElevated()
    {
        var service = new ModelGovernanceService();
        var now = DateTime.UtcNow;
        var signals = Enumerable.Range(0, 12)
            .Select(index => CreateSignal(
                "model-live",
                now.AddMinutes(-index),
                drift: true,
                veto: index < 4))
            .ToArray();
        var trades = new[]
        {
            CreateTrade("model-live", -6m, now.AddMinutes(-30)),
            CreateTrade("model-live", -4m, now.AddMinutes(-25)),
            CreateTrade("model-live", 2m, now.AddMinutes(-20)),
            CreateTrade("model-prev", 12m, now.AddHours(-2)),
            CreateTrade("model-prev", 10m, now.AddHours(-3)),
            CreateTrade("model-prev", 8m, now.AddHours(-4)),
            CreateTrade("model-prev", 6m, now.AddHours(-5))
        };

        var trigger = service.EvaluateRetrainingNeed(signals, trades, "model-live");

        trigger.TriggerRetrain.Should().BeTrue();
        trigger.Summary.Should().Contain("sustained elevated drift");
        trigger.Summary.Should().Contain("repeated live vetoes");
    }

    private static Signal CreateSignal(string modelVersion, DateTime timestamp, bool drift, bool veto)
    {
        var signal = new Signal
        {
            Symbol = "BTCUSDT",
            Timestamp = timestamp,
            Status = veto ? TradeStatus.Rejected : TradeStatus.Executed,
            ReasonFlags = veto ? SignalReasonFlags.MachineLearningVeto : SignalReasonFlags.None
        };
        signal.Metadata["MlModelVersion"] = modelVersion;
        if (drift)
        {
            signal.Metadata["FeatureDriftDetected"] = "true";
            signal.Metadata["FeatureDriftCritical"] = veto ? "true" : "false";
            signal.Metadata["FeatureDriftImpactLabel"] = veto ? "ML drift vetoed setup" : "ML drift trimmed size";
        }

        return signal;
    }

    private static Trade CreateTrade(string modelVersion, decimal pnl, DateTime exitTime) => new()
    {
        Symbol = "BTCUSDT",
        Side = TradeSide.Buy,
        EntryPrice = 100m,
        ExitPrice = 100m + pnl,
        Quantity = 1m,
        Status = TradeStatus.Executed,
        ExecutionMode = "REAL",
        MlModelVersion = modelVersion,
        EntryTime = exitTime.AddMinutes(-30),
        ExitTime = exitTime
    };
}
