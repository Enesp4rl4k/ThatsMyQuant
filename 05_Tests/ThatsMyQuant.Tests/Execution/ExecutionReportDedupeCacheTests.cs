using FluentAssertions;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Services;
using Xunit;

namespace ThatsMyQuant.Tests.Execution;

public class ExecutionReportDedupeCacheTests
{
    [Fact]
    public void ShouldProcess_ShouldRejectSameExchangeOrderAndLastFillDuplicate()
    {
        var cache = new ExecutionReportDedupeCache(maxEntries: 512);
        var report = NewReport("ord-1", 0.25m, "PARTIALLY_FILLED");

        cache.ShouldProcess(report).Should().BeTrue();
        cache.ShouldProcess(report).Should().BeFalse();
    }

    [Fact]
    public void ShouldProcess_ShouldAllowDifferentLastFillQuantityForSameOrder()
    {
        var cache = new ExecutionReportDedupeCache(maxEntries: 512);
        var first = NewReport("ord-1", 0.25m, "PARTIALLY_FILLED");
        var next = NewReport("ord-1", 0.50m, "PARTIALLY_FILLED");

        cache.ShouldProcess(first).Should().BeTrue();
        cache.ShouldProcess(next).Should().BeTrue();
    }

    [Fact]
    public void ShouldProcess_ShouldEvictOldEntriesWhenCapacityExceeded()
    {
        var cache = new ExecutionReportDedupeCache(maxEntries: 128);
        for (var i = 0; i < 300; i++)
        {
            cache.ShouldProcess(NewReport($"ord-{i}", 0.1m, "NEW")).Should().BeTrue();
        }

        // Oldest entries should have been evicted, so this can re-enter.
        cache.ShouldProcess(NewReport("ord-0", 0.1m, "NEW")).Should().BeTrue();
    }

    private static ExecutionReport NewReport(string exchangeOrderId, decimal lastFillQuantity, string status)
        => new(
            Symbol: "BTCUSDT",
            ClientOrderId: "tmq-signal-1",
            ExchangeOrderId: exchangeOrderId,
            FilledQuantity: lastFillQuantity,
            RemainingQuantity: Math.Max(0m, 1m - lastFillQuantity),
            AveragePrice: 100m,
            LastFillPrice: 100m,
            LastFillQuantity: lastFillQuantity,
            Status: status,
            LatencyMs: 5,
            TimestampUtc: DateTime.UtcNow);
}
