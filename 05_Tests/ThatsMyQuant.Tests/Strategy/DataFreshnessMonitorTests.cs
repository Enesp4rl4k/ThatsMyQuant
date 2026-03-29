using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

/// <summary>
/// Unit tests for DataFreshnessMonitor: stale detection, critical gap alerts,
/// per-symbol tracking, and recovery transitions.
///
/// Note: RecordTick/RecordOrderBook use wall-clock time (DateTimeOffset.UtcNow),
/// not the tick's own timestamp. To simulate stale data, we inject old timestamps
/// directly into the internal dictionary.
/// </summary>
public sealed class DataFreshnessMonitorTests
{
    private static DataFreshnessMonitor CreateMonitor(int staleThreshold = 30, int criticalGap = 300)
    {
        var monitor = new DataFreshnessMonitor(Substitute.For<ILogger<DataFreshnessMonitor>>())
        {
            StaleThresholdSeconds = staleThreshold,
            CriticalGapSeconds = criticalGap
        };
        return monitor;
    }

    /// <summary>
    /// Injects a synthetic last-tick timestamp directly into the monitor's internal dictionary.
    /// Used to simulate stale data without waiting real time.
    /// </summary>
    private static void InjectLastTickMs(DataFreshnessMonitor monitor, string symbol, long timestampMs)
    {
        var field = typeof(DataFreshnessMonitor).GetField("_lastTickMs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (ConcurrentDictionary<string, long>)field.GetValue(monitor)!;
        dict[symbol] = timestampMs;
    }

    // ─── Fresh data ──────────────────────────────────────────────────────────

    [Fact]
    public void CheckFreshness_ReturnsFresh_AfterRecentTick()
    {
        var monitor = CreateMonitor();
        monitor.RecordTick("BTCUSDT", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var (isFresh, gapSeconds, reason) = monitor.CheckFreshness("BTCUSDT");

        isFresh.Should().BeTrue();
        gapSeconds.Should().BeLessThan(5);
        reason.Should().Contain("fresh");
    }

    [Fact]
    public void CheckFreshness_ReturnsFresh_AfterRecentOrderBook()
    {
        var monitor = CreateMonitor();
        monitor.RecordOrderBook("ETHUSDT", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var (isFresh, _, _) = monitor.CheckFreshness("ETHUSDT");

        isFresh.Should().BeTrue();
    }

    // ─── No data ─────────────────────────────────────────────────────────────

    [Fact]
    public void CheckFreshness_ReturnsNotFresh_WhenNoDataReceived()
    {
        var monitor = CreateMonitor();

        var (isFresh, gapSeconds, reason) = monitor.CheckFreshness("UNKNOWN");

        isFresh.Should().BeFalse();
        gapSeconds.Should().Be(double.MaxValue);
        reason.Should().Contain("No data ever received");
    }

    // ─── Stale data ──────────────────────────────────────────────────────────

    [Fact]
    public void CheckFreshness_DetectsStale_WhenTickTooOld()
    {
        var monitor = CreateMonitor(staleThreshold: 5);
        // Inject a timestamp 10 seconds in the past
        var oldMs = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeMilliseconds();
        InjectLastTickMs(monitor, "BTCUSDT", oldMs);

        var (isFresh, gapSeconds, reason) = monitor.CheckFreshness("BTCUSDT");

        isFresh.Should().BeFalse();
        gapSeconds.Should().BeGreaterOrEqualTo(5);
        reason.Should().Contain("Stale");
    }

    // ─── Critical gap ────────────────────────────────────────────────────────

    [Fact]
    public void CheckFreshness_DetectsCriticalGap_WhenTickVeryOld()
    {
        var monitor = CreateMonitor(staleThreshold: 5, criticalGap: 30);
        var oldMs = DateTimeOffset.UtcNow.AddSeconds(-60).ToUnixTimeMilliseconds();
        InjectLastTickMs(monitor, "BTCUSDT", oldMs);

        var (isFresh, gapSeconds, reason) = monitor.CheckFreshness("BTCUSDT");

        isFresh.Should().BeFalse();
        gapSeconds.Should().BeGreaterOrEqualTo(30);
        reason.Should().Contain("Critical");
    }

    // ─── Per-symbol isolation ────────────────────────────────────────────────

    [Fact]
    public void CheckFreshness_TracksSymbolsIndependently()
    {
        var monitor = CreateMonitor(staleThreshold: 5);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var oldMs = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeMilliseconds();

        // Fresh symbol: record normally (uses wall clock = now)
        monitor.RecordTick("BTCUSDT", nowMs);
        // Stale symbol: inject old timestamp directly
        InjectLastTickMs(monitor, "ETHUSDT", oldMs);

        monitor.CheckFreshness("BTCUSDT").IsFresh.Should().BeTrue();
        monitor.CheckFreshness("ETHUSDT").IsFresh.Should().BeFalse();
    }

    // ─── Case insensitive ────────────────────────────────────────────────────

    [Fact]
    public void RecordTick_IsCaseInsensitive()
    {
        var monitor = CreateMonitor();
        monitor.RecordTick("btcusdt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        monitor.CheckFreshness("BTCUSDT").IsFresh.Should().BeTrue();
    }

    // ─── HasAnyStaleData ─────────────────────────────────────────────────────

    [Fact]
    public void HasAnyStaleData_ReturnsFalse_WhenAllFresh()
    {
        var monitor = CreateMonitor();
        monitor.RecordTick("BTCUSDT", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        monitor.RecordTick("ETHUSDT", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        monitor.HasAnyStaleData().Should().BeFalse();
    }

    [Fact]
    public void HasAnyStaleData_ReturnsTrue_WhenOneSymbolStale()
    {
        var monitor = CreateMonitor(staleThreshold: 5);
        monitor.RecordTick("BTCUSDT", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        InjectLastTickMs(monitor, "ETHUSDT", DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeMilliseconds());

        monitor.HasAnyStaleData().Should().BeTrue();
    }

    [Fact]
    public void HasAnyStaleData_ReturnsFalse_WhenNoSymbolsTracked()
    {
        var monitor = CreateMonitor();

        monitor.HasAnyStaleData().Should().BeFalse();
    }

    // ─── GetAllStatus ────────────────────────────────────────────────────────

    [Fact]
    public void GetAllStatus_ReturnsAllTrackedSymbols()
    {
        var monitor = CreateMonitor();
        monitor.RecordTick("BTCUSDT", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        monitor.RecordTick("ETHUSDT", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var status = monitor.GetAllStatus();

        status.Should().ContainKeys("BTCUSDT", "ETHUSDT");
        status["BTCUSDT"].IsFresh.Should().BeTrue();
        status["ETHUSDT"].IsFresh.Should().BeTrue();
    }

    [Fact]
    public void GetAllStatus_ReturnsEmpty_WhenNoSymbolsTracked()
    {
        var monitor = CreateMonitor();

        monitor.GetAllStatus().Should().BeEmpty();
    }

    // ─── Recovery transition ─────────────────────────────────────────────────

    [Fact]
    public void RecordTick_ClearsStaleState_WhenDataRecovers()
    {
        var monitor = CreateMonitor(staleThreshold: 5);

        // First: make it stale by injecting old timestamp
        InjectLastTickMs(monitor, "BTCUSDT", DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeMilliseconds());
        monitor.CheckFreshness("BTCUSDT").IsFresh.Should().BeFalse();

        // Then: recovery via real tick (records wall-clock now)
        monitor.RecordTick("BTCUSDT", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        monitor.CheckFreshness("BTCUSDT").IsFresh.Should().BeTrue();
    }

    // ─── Configurable thresholds ─────────────────────────────────────────────

    [Fact]
    public void StaleThreshold_IsConfigurable()
    {
        var monitor = CreateMonitor(staleThreshold: 1);
        InjectLastTickMs(monitor, "BTCUSDT", DateTimeOffset.UtcNow.AddSeconds(-2).ToUnixTimeMilliseconds());

        monitor.CheckFreshness("BTCUSDT").IsFresh.Should().BeFalse(
            "1-second threshold should flag 2-second-old data as stale");
    }

    [Fact]
    public void CriticalGapThreshold_IsConfigurable()
    {
        var monitor = CreateMonitor(staleThreshold: 1, criticalGap: 5);
        InjectLastTickMs(monitor, "BTCUSDT", DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeMilliseconds());

        var (_, _, reason) = monitor.CheckFreshness("BTCUSDT");
        reason.Should().Contain("Critical");
    }
}
