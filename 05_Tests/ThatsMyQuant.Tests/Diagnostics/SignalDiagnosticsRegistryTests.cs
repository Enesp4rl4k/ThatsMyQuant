using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ThatsMyQuant.Application.Diagnostics;
using ThatsMyQuant.Infrastructure.Diagnostics;
using Xunit;

namespace ThatsMyQuant.Tests.Diagnostics;

public class SignalDiagnosticsRegistryTests
{
    private static SignalDiagnosticSnapshot Snap(
        string tier, string key, DateTime ts,
        decimal current = 0.0001m, SignalDecision decision = SignalDecision.BelowThreshold)
        => new(
            UtcTimestamp: ts,
            Tier: tier,
            StrategyTag: tier,
            Key: key,
            CurrentValue: current,
            EntryThreshold: 0.0001m,
            ExitThreshold: null,
            StopThreshold: null,
            DistanceToTriggerPct: 100m,
            Decision: decision,
            SkipOrSuppressReason: null,
            RegimeContext: null,
            HasOpenPosition: false,
            EstimatedNotionalIfFired: null);

    [Fact]
    public void Publish_NewKey_AddsRow()
    {
        var reg = new SignalDiagnosticsRegistry();
        reg.Publish(Snap("Funding:Long", "LTCUSDT", DateTime.UtcNow));

        reg.Count.Should().Be(1);
        reg.GetAll().Should().ContainSingle(s => s.Key == "LTCUSDT");
    }

    [Fact]
    public void Publish_SameKeyTwice_ReplacesWhenNewer()
    {
        var reg = new SignalDiagnosticsRegistry();
        var t0 = DateTime.UtcNow;
        reg.Publish(Snap("Funding:Long", "LTCUSDT", t0, current: 0.0001m));
        reg.Publish(Snap("Funding:Long", "LTCUSDT", t0.AddSeconds(5), current: 0.0005m));

        reg.Count.Should().Be(1);
        reg.GetAll().Single().CurrentValue.Should().Be(0.0005m);
    }

    [Fact]
    public void Publish_OlderTimestamp_DoesNotClobber()
    {
        // Slow thread published late but its evaluation is older than what's in the
        // registry — the newer reading must win.
        var reg = new SignalDiagnosticsRegistry();
        var t0 = DateTime.UtcNow;
        reg.Publish(Snap("Funding:Long", "LTCUSDT", t0.AddSeconds(5), current: 0.0005m));
        reg.Publish(Snap("Funding:Long", "LTCUSDT", t0,                current: 0.0001m));

        reg.GetAll().Single().CurrentValue.Should().Be(0.0005m);
    }

    [Fact]
    public void Publish_DifferentTiersSameKey_StoredSeparately()
    {
        // (Funding:Long, BTCUSDT) and (EdgeLab:single_asset, BTCUSDT) must coexist —
        // a single symbol can be evaluated by multiple tiers in parallel.
        var reg = new SignalDiagnosticsRegistry();
        var now = DateTime.UtcNow;
        reg.Publish(Snap("Funding:Long",         "BTCUSDT", now));
        reg.Publish(Snap("EdgeLab:single_asset", "BTCUSDT", now));

        reg.Count.Should().Be(2);
    }

    [Fact]
    public void GetForTier_FiltersCaseInsensitive()
    {
        var reg = new SignalDiagnosticsRegistry();
        var now = DateTime.UtcNow;
        reg.Publish(Snap("Funding:Long",  "LTCUSDT", now));
        reg.Publish(Snap("Funding:Long",  "BTCUSDT", now));
        reg.Publish(Snap("Pairs:Long",    "ADA-XRP", now));

        reg.GetForTier("funding:long").Should().HaveCount(2);
        reg.GetForTier("Pairs:Long").Should().HaveCount(1);
        reg.GetForTier("does-not-exist").Should().BeEmpty();
    }

    [Fact]
    public void GetForTier_NullOrWhitespace_ReturnsEmpty()
    {
        var reg = new SignalDiagnosticsRegistry();
        reg.Publish(Snap("Funding:Long", "LTCUSDT", DateTime.UtcNow));

        reg.GetForTier(null!).Should().BeEmpty();
        reg.GetForTier("   ").Should().BeEmpty();
    }

    [Fact]
    public void Publish_NullSnapshot_Throws()
    {
        var reg = new SignalDiagnosticsRegistry();
        var act = () => reg.Publish(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Publish_HighConcurrency_LastWriteByTimestampWins()
    {
        // Hammer the same key from many threads; the snapshot with the latest
        // timestamp must end up as the persisted value regardless of arrival order.
        var reg = new SignalDiagnosticsRegistry();
        var baseTs = DateTime.UtcNow;
        const int n = 1000;

        var tasks = Enumerable.Range(0, n)
            .Select(i => Task.Run(() =>
                reg.Publish(Snap("Funding:Long", "LTCUSDT", baseTs.AddMilliseconds(i),
                    current: i * 0.00001m))))
            .ToArray();

        await Task.WhenAll(tasks);

        reg.Count.Should().Be(1);
        var final = reg.GetAll().Single();
        final.UtcTimestamp.Should().Be(baseTs.AddMilliseconds(n - 1));
        final.CurrentValue.Should().Be((n - 1) * 0.00001m);
    }
}
