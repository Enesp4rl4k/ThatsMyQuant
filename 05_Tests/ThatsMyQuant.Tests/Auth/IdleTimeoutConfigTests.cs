// Feature: session-logout, Property 1: Config invariant enforcement
// Feature: session-logout, Property 2: Valid config construction round-trip
// Validates: Requirements 2.3, 7.5

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using ThatsMyQuant.Dashboard.Auth;

namespace ThatsMyQuant.Tests.Auth;

public class IdleTimeoutConfigTests
{
    // ── Property 1: Invariant enforcement ────────────────────────────────────
    // For all (ts, wl) where wl >= ts && ts > 0, constructor throws ArgumentException.
    // Validates: Requirements 2.3, 7.5
    [Property(MaxTest = 100)]
    public Property P1_InvalidConfig_AlwaysThrows()
    {
        // ts > 0, wl >= ts  →  must throw
        var gen =
            from ts in Gen.Choose(1, 10_000)
            from wl in Gen.Choose(ts, ts + 10_000)
            select (ts, wl);

        return Prop.ForAll(gen.ToArbitrary(), pair =>
        {
            var (ts, wl) = pair;
            try
            {
                _ = new IdleTimeoutConfig(ts, wl);
                return false; // should have thrown
            }
            catch (ArgumentException)
            {
                return true;
            }
        });
    }

    // ── Property 2: Valid construction round-trip ─────────────────────────────
    // For all (ts, wl) where 0 < wl < ts, construction succeeds and properties round-trip.
    // Validates: Requirements 2.1, 2.2, 2.3
    [Property(MaxTest = 100)]
    public Property P2_ValidConfig_RoundTrips()
    {
        // ts > wl > 0  →  must succeed and expose the same values
        var gen =
            from ts in Gen.Choose(2, 10_000)
            from wl in Gen.Choose(1, ts - 1)
            select (ts, wl);

        return Prop.ForAll(gen.ToArbitrary(), pair =>
        {
            var (ts, wl) = pair;
            var cfg = new IdleTimeoutConfig(ts, wl);
            return cfg.TimeoutSeconds == ts && cfg.WarningLeadSeconds == wl;
        });
    }
}
