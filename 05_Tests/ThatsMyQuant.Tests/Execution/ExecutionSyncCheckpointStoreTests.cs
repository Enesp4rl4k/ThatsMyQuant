using FluentAssertions;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Execution;

public class ExecutionSyncCheckpointStoreTests
{
    [Fact]
    public void ShouldApply_ShouldRejectDuplicateAndOlderReports()
    {
        var checkpointPath = Path.Combine(Path.GetTempPath(), $"tmq-sync-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ExecutionSyncCheckpointStore(checkpointPath);
            var report = NewReport("ord-1", DateTime.UtcNow, "PARTIALLY_FILLED", 0.4m, 0.6m);

            store.Evaluate(report).Should().Be(ExecutionSyncCheckpointStore.CheckpointDecision.Apply);
            store.Commit(report);

            store.Evaluate(report).Should().Be(ExecutionSyncCheckpointStore.CheckpointDecision.Duplicate, "exact duplicate should be ignored");

            var older = NewReport("ord-1", report.TimestampUtc.AddSeconds(-1), "FILLED", 1.0m, 0m);
            store.Evaluate(older).Should().Be(ExecutionSyncCheckpointStore.CheckpointDecision.OutOfOrder, "older report should be ignored");

            var newer = NewReport("ord-1", report.TimestampUtc.AddSeconds(1), "FILLED", 1.0m, 0m);
            store.Evaluate(newer).Should().Be(ExecutionSyncCheckpointStore.CheckpointDecision.Apply, "newer report should be applied");
        }
        finally
        {
            if (File.Exists(checkpointPath))
            {
                File.Delete(checkpointPath);
            }
        }
    }

    [Fact]
    public void Evaluate_ShouldRejectSameTimestampRegression()
    {
        var checkpointPath = Path.Combine(Path.GetTempPath(), $"tmq-sync-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ExecutionSyncCheckpointStore(checkpointPath);
            var report = NewReport("ord-2", DateTime.UtcNow, "PARTIALLY_FILLED", 0.8m, 0.2m);
            store.Commit(report);

            var regressedAtSameTs = NewReport("ord-2", report.TimestampUtc, "PARTIALLY_FILLED", 0.6m, 0.4m);
            store.Evaluate(regressedAtSameTs).Should().Be(ExecutionSyncCheckpointStore.CheckpointDecision.OutOfOrder);
        }
        finally
        {
            if (File.Exists(checkpointPath))
            {
                File.Delete(checkpointPath);
            }
        }
    }

    private static ExecutionReport NewReport(string clientOrderId, DateTime ts, string status, decimal filled, decimal remaining)
        => new(
            "BTCUSDT",
            clientOrderId,
            "exch-1",
            filled,
            remaining,
            100m,
            100m,
            0.1m,
            status,
            5,
            ts);
}
