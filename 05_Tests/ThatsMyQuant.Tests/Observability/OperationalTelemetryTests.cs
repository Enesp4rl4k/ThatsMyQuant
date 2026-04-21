using FluentAssertions;
using ThatsMyQuant.Application.Observability;
using Xunit;

namespace ThatsMyQuant.Tests.Observability;

public class OperationalTelemetryTests
{
    [Fact]
    public void RecordMethods_ShouldPopulateSnapshot()
    {
        var sut = new OperationalTelemetry("Tests");

        sut.RecordSignalQueued("BTCUSDT");
        sut.RecordSignalRejected("ETHUSDT");
        sut.RecordTradeSucceeded(125, "BTCUSDT");
        sut.RecordTradeSucceeded(75, "BTCUSDT");
        sut.RecordTradeFailed("XRPUSDT");
        sut.RecordDeadLetter("ADAUSDT");
        sut.RecordLifecycleSupervision(3);
        sut.RecordEntrySlippagePercent(0.5m, "BTCUSDT");
        sut.RecordEntrySlippagePercent(-1.5m, "BTCUSDT");

        var snapshot = sut.GetSnapshot();

        snapshot.ServiceName.Should().Be("Tests");
        snapshot.SignalsQueued.Should().Be(1);
        snapshot.SignalsRejected.Should().Be(1);
        snapshot.TradesSucceeded.Should().Be(2);
        snapshot.TradesFailed.Should().Be(1);
        snapshot.DeadLetters.Should().Be(1);
        snapshot.LifecycleSupervisionPasses.Should().Be(3);
        snapshot.AverageExecutionLatencyMs.Should().Be(100);
        snapshot.LastExecutionLatencyMs.Should().Be(75);
        snapshot.EntrySlippageSamples.Should().Be(2);
        snapshot.AverageAbsEntrySlippagePercent.Should().Be(1);
        snapshot.LastAbsEntrySlippagePercent.Should().Be(1.5);
    }
}
