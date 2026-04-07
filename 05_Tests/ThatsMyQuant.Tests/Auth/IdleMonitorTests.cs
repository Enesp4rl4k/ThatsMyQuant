// Feature: session-logout, Property 3: Warning precedes elapsed
// Feature: session-logout, Property 4: Reset restores full timeout
// Validates: Requirements 3.2, 3.3, 3.4, 7.4, 7.8

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using ThatsMyQuant.Dashboard.Auth;

namespace ThatsMyQuant.Tests.Auth;

// ── Fake ticker ───────────────────────────────────────────────────────────────
// Allows tests to drive ticks synchronously without real time passing.

internal sealed class FakeTicker : IIdleTicker
{
    public event EventHandler? Tick;
    public bool IsRunning { get; private set; }

    public void Start() => IsRunning = true;
    public void Stop()  => IsRunning = false;

    /// <summary>Fire <paramref name="count"/> ticks synchronously.</summary>
    public void FireTicks(int count)
    {
        for (int i = 0; i < count; i++)
            Tick?.Invoke(this, EventArgs.Empty);
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public class IdleMonitorTests
{
    // ── Property 3: Warning precedes elapsed ─────────────────────────────────
    // For all valid configs where TimeoutSeconds > 0 and WarningLeadSeconds > 0,
    // IdleWarning is always raised before IdleElapsed in the same idle cycle.
    // Validates: Requirements 3.3, 3.4, 7.4
    [Property(MaxTest = 100)]
    public Property P3_IdleWarning_AlwaysPrecedesIdleElapsed()
    {
        // Generate valid configs: ts in [2..20], wl in [1..ts-1]
        var gen =
            from ts in Gen.Choose(2, 20)
            from wl in Gen.Choose(1, ts - 1)
            select new IdleTimeoutConfig(ts, wl);

        return Prop.ForAll(gen.ToArbitrary(), config =>
        {
            var ticker  = new FakeTicker();
            var monitor = new IdleMonitor(config, ticker);

            int warningOrder = -1;
            int elapsedOrder = -1;
            int eventIndex   = 0;

            monitor.IdleWarning += (_, _) => warningOrder = eventIndex++;
            monitor.IdleElapsed += (_, _) => elapsedOrder = eventIndex++;

            monitor.Start();

            // Drive ticks up to TimeoutSeconds to complete one full cycle
            ticker.FireTicks(config.TimeoutSeconds);

            // Both events must have fired and warning must come first
            return warningOrder >= 0
                && elapsedOrder  >= 0
                && warningOrder  <  elapsedOrder;
        });
    }

    // ── Property 4: Reset restores full timeout ───────────────────────────────
    // After Reset(), the number of ticks until the next IdleElapsed equals
    // TimeoutSeconds (within ±0 ticks when using a fake synchronous ticker).
    // Validates: Requirements 3.2, 7.8
    [Property(MaxTest = 100)]
    public Property P4_Reset_RestoresFullTimeout()
    {
        // Generate valid configs: ts in [2..20], wl in [1..ts-1]
        var gen =
            from ts in Gen.Choose(2, 20)
            from wl in Gen.Choose(1, ts - 1)
            // resetAt: tick count before reset, in [1..ts-1] (before elapsed fires)
            from resetAt in Gen.Choose(1, ts - 1)
            select (config: new IdleTimeoutConfig(ts, wl), resetAt);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (config, resetAt) = tuple;
            var ticker  = new FakeTicker();
            var monitor = new IdleMonitor(config, ticker);

            int elapsedFiredCount = 0;
            monitor.IdleElapsed += (_, _) => elapsedFiredCount++;

            monitor.Start();

            // Advance to resetAt ticks (before timeout), then reset
            ticker.FireTicks(resetAt);
            monitor.Reset();

            // After reset, IdleElapsed should NOT have fired yet
            if (elapsedFiredCount != 0) return false;

            // Now drive exactly TimeoutSeconds more ticks — elapsed must fire exactly once
            ticker.FireTicks(config.TimeoutSeconds);

            return elapsedFiredCount == 1;
        });
    }

    // ── Unit test: disabled monitor never fires ───────────────────────────────
    [Xunit.Fact]
    public void DisabledMonitor_NeverStartsTicker()
    {
        var config  = IdleTimeoutConfig.Disabled;
        var ticker  = new FakeTicker();
        var monitor = new IdleMonitor(config, ticker);

        monitor.Start();

        Xunit.Assert.False(ticker.IsRunning);
    }

    // ── Unit test: warning fires at correct tick ──────────────────────────────
    [Xunit.Fact]
    public void IdleWarning_FiresAtExpectedTick()
    {
        var config  = new IdleTimeoutConfig(timeoutSeconds: 5, warningLeadSeconds: 2);
        var ticker  = new FakeTicker();
        var monitor = new IdleMonitor(config, ticker);

        bool warningFired = false;
        monitor.IdleWarning += (_, _) => warningFired = true;

        monitor.Start();
        ticker.FireTicks(3); // warning at tick 3 (5 - 2)

        Xunit.Assert.True(warningFired);
    }
}
