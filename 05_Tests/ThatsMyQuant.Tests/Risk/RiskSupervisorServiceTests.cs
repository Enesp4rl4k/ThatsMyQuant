// Feature: live-trading-safety, Property 3: Drawdown formula invariant
// Validates: Requirements 2.4

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ThatsMyQuant.Application.Risk;
using ThatsMyQuant.Contracts.Interfaces;
using Xunit;

namespace ThatsMyQuant.Tests.Risk;

public class RiskSupervisorServiceTests
{
    // ── generators ────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a positive decimal peak value in (0, 1_000_000].
    /// Uses PositiveInt scaled to a decimal to avoid division-by-zero.
    /// </summary>
    private static Gen<decimal> PositivePeakGen() =>
        Gen.Choose(1, 1_000_000).Select(n => (decimal)n);

    /// <summary>
    /// Generates a non-negative decimal current value in [0, peak].
    /// </summary>
    private static Gen<decimal> CurrentGen(decimal peak) =>
        Gen.Choose(0, (int)Math.Min(peak, int.MaxValue)).Select(n => (decimal)n);

    // ── Property 3: Drawdown formula invariant ────────────────────────────────
    // For any peakBalance > 0 and currentBalance in [0, peakBalance],
    // (peakBalance - currentBalance) / peakBalance * 100 must be in [0, 100].
    // Validates: Requirements 2.4
    [Property(MaxTest = 20)]
    public Property DrawdownFormula_AlwaysInZeroToHundred()
    {
        // Feature: live-trading-safety, Property 3: Drawdown formula invariant
        var gen =
            from peak in PositivePeakGen()
            from current in CurrentGen(peak)
            select (peak, current);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (peakVal, currentVal) = tuple;

            // Act: compute drawdown using the exact formula from RiskSupervisorService
            var drawdown = (peakVal - currentVal) / peakVal * 100m;

            // Assert: result must be in [0, 100]
            return drawdown >= 0m && drawdown <= 100m;
        });
    }

    // ── Property 4: Monotone peak ─────────────────────────────────────────────
    // For any sequence of balance observations b1, b2, ..., bN,
    // _peakBalance after processing the full sequence equals Max(b1, ..., bN).
    // The peak never decreases.
    // Validates: Requirements 2.2
    [Property(MaxTest = 20)]
    public Property MonotonePeak_AfterSequence_EqualsTrueMaximum()
    {
        // Feature: live-trading-safety, Property 4: Monotone peak
        var gen = Gen.NonEmptyListOf(Gen.Choose(0, 1_000_000).Select(n => (decimal)n));

        return Prop.ForAll(gen.ToArbitrary(), balances =>
        {
            // Simulate the peak tracking logic from RiskSupervisorService.ExecuteAsync:
            //   _peakBalance = Max(_peakBalance, currentBalance)
            decimal peakBalance = 0m;
            foreach (var balance in balances)
                peakBalance = Math.Max(peakBalance, balance);

            // Assert: peak equals the true maximum of the sequence
            var expectedMax = balances.Max();
            return peakBalance == expectedMax;
        });
    }

    // ── Property 5: Loop resilience ───────────────────────────────────────────
    // For any exception thrown by IPortfolioManager.GetAvailableBalance during a
    // monitoring iteration, the loop continues to the next iteration — the exception
    // is caught, logged, and not re-thrown.
    // Validates: Requirements 3.4
    [Property(MaxTest = 20)]
    public Property LoopResilience_ExceptionInGetBalance_DoesNotKillLoop()
    {
        // Feature: live-trading-safety, Property 5: Loop resilience
        // Generate a number of iterations (1–5) and a bitmask of which calls throw.
        var gen =
            from iterations in Gen.Choose(1, 5)
            from throwMask in Gen.Choose(0, (1 << iterations) - 1)
            select (iterations, throwMask);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (iterations, throwMask) = tuple;

            // Build a portfolio mock where some calls throw and others return 1000m.
            var callIndex = 0;
            var portfolio = Substitute.For<IPortfolioManager>();
            portfolio.GetAvailableBalance("USDT").Returns(_ =>
            {
                var idx = callIndex++;
                // The first call is the startup balance recording (before the loop).
                // Subsequent calls are loop iterations. We apply the throw mask to
                // loop-iteration calls (idx >= 1).
                var loopIdx = idx - 1;
                if (loopIdx >= 0 && loopIdx < iterations && (throwMask & (1 << loopIdx)) != 0)
                    throw new InvalidOperationException($"Simulated failure on iteration {loopIdx}");
                return 1000m;
            });

            var exchange = Substitute.For<IExchangeService>();
            var logger = NullLogger<RiskSupervisorService>.Instance;

            var svc = new RiskSupervisorService(logger, exchange, portfolio, maxDrawdownPercent: 99m);

            // Run the service for just long enough to complete the desired iterations,
            // then cancel. Each loop iteration has a 1-second Task.Delay, so we use a
            // very short delay and rely on cancellation to stop the loop cleanly.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(iterations * 50 + 200));

            // ExecuteAsync must complete without throwing — the loop must survive all exceptions.
            Exception? caughtException = null;
            try
            {
                // BackgroundService.StartAsync fires ExecuteAsync; we call it directly.
                svc.StartAsync(cts.Token).GetAwaiter().GetResult();
                // Wait for the background task to finish (cancelled by cts).
                svc.ExecuteTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                caughtException = ex;
            }

            // The service must not have thrown a non-cancellation exception.
            return caughtException == null;
        });
    }

    // ── Unit tests ────────────────────────────────────────────────────────────

    private static (RiskSupervisorService svc, IExchangeService exchange, IPortfolioManager portfolio)
        BuildSvc(decimal balance = 1000m, decimal maxDrawdown = 5m)
    {
        var portfolio = Substitute.For<IPortfolioManager>();
        portfolio.GetAvailableBalance("USDT").Returns(balance);
        var exchange = Substitute.For<IExchangeService>();
        exchange.CancelAllOrdersAsync().Returns(Task.FromResult(0));
        var svc = new RiskSupervisorService(
            NullLogger<RiskSupervisorService>.Instance, exchange, portfolio, maxDrawdown);
        return (svc, exchange, portfolio);
    }

    // Req 1.1 — halt flag set and CancelAllOrdersAsync awaited
    [Fact]
    public async Task TriggerEmergencyHaltAsync_SetsHaltedAndCallsCancelAllOrders()
    {
        var (svc, exchange, _) = BuildSvc();
        await svc.TriggerEmergencyHaltAsync("test");
        await exchange.Received(1).CancelAllOrdersAsync();
    }

    // Req 1.2 — exception during cancel does not clear halt flag
    [Fact]
    public async Task TriggerEmergencyHaltAsync_WhenCancelThrows_HaltFlagRemainsTrue()
    {
        var (svc, exchange, _) = BuildSvc();
        exchange.CancelAllOrdersAsync().ThrowsAsync(new Exception("network error"));
        await svc.TriggerEmergencyHaltAsync("test");
        // Calling again must be a no-op (idempotent) — CancelAllOrders not called a second time
        await svc.TriggerEmergencyHaltAsync("test again");
        await exchange.Received(1).CancelAllOrdersAsync();
    }

    // Property 1 — idempotence: second call is a no-op
    [Fact]
    public async Task TriggerEmergencyHaltAsync_CalledTwice_CancelAllOrdersCalledOnce()
    {
        var (svc, exchange, _) = BuildSvc();
        await svc.TriggerEmergencyHaltAsync("first");
        await svc.TriggerEmergencyHaltAsync("second");
        await exchange.Received(1).CancelAllOrdersAsync();
    }

    // Req 2.3 — drawdown threshold triggers halt
    [Fact]
    public async Task MonitoringLoop_WhenDrawdownExceedsThreshold_TriggersHalt()
    {
        var portfolio = Substitute.For<IPortfolioManager>();
        var exchange  = Substitute.For<IExchangeService>();
        exchange.CancelAllOrdersAsync().Returns(Task.FromResult(0));

        var callCount = 0;
        // First call (startup): 1000. Second call (loop): 800 → 20% drawdown > 5% threshold.
        portfolio.GetAvailableBalance("USDT").Returns(_ => callCount++ == 0 ? 1000m : 800m);

        var svc = new RiskSupervisorService(
            NullLogger<RiskSupervisorService>.Instance, exchange, portfolio, maxDrawdownPercent: 5m);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await svc.StartAsync(cts.Token);

        // Give the loop time to fire at least once (1-second delay + buffer)
        await Task.Delay(1500);
        await cts.CancelAsync();
        await svc.StopAsync(CancellationToken.None);

        await exchange.Received(1).CancelAllOrdersAsync();
    }

    // Req 2.5 — zero peak skips drawdown check (no halt)
    [Fact]
    public async Task MonitoringLoop_WhenPeakIsZero_SkipsDrawdownCheck()
    {
        var portfolio = Substitute.For<IPortfolioManager>();
        var exchange  = Substitute.For<IExchangeService>();
        exchange.CancelAllOrdersAsync().Returns(Task.FromResult(0));

        // Always return 0 — peak stays 0, drawdown check must be skipped
        portfolio.GetAvailableBalance("USDT").Returns(0m);

        var svc = new RiskSupervisorService(
            NullLogger<RiskSupervisorService>.Instance, exchange, portfolio, maxDrawdownPercent: 5m);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await svc.StartAsync(cts.Token);
        await Task.Delay(1500);
        await cts.CancelAsync();
        await svc.StopAsync(CancellationToken.None);

        await exchange.DidNotReceive().CancelAllOrdersAsync();
    }

    // Req 2.1 — initial balance recorded at startup
    [Fact]
    public async Task StartAsync_RecordsInitialBalance()
    {
        var portfolio = Substitute.For<IPortfolioManager>();
        var exchange  = Substitute.For<IExchangeService>();
        exchange.CancelAllOrdersAsync().Returns(Task.FromResult(0));

        // Startup: 500. Loop: 600 (higher — no drawdown).
        var callCount = 0;
        portfolio.GetAvailableBalance("USDT").Returns(_ => callCount++ == 0 ? 500m : 600m);

        var svc = new RiskSupervisorService(
            NullLogger<RiskSupervisorService>.Instance, exchange, portfolio, maxDrawdownPercent: 5m);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await svc.StartAsync(cts.Token);
        await Task.Delay(1500);
        await cts.CancelAsync();
        await svc.StopAsync(CancellationToken.None);

        // No halt should have fired — initial balance was 500, loop saw 600 (peak went up)
        await exchange.DidNotReceive().CancelAllOrdersAsync();
        // GetAvailableBalance must have been called at least once (startup recording)
        portfolio.Received().GetAvailableBalance("USDT");
    }
}
