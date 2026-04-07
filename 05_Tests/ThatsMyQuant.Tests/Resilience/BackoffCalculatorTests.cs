// Feature: execution-hardening, Property 1: backoff delay bounds
using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using ThatsMyQuant.Application.Resilience;
using Xunit;

namespace ThatsMyQuant.Tests.Resilience;

public class BackoffCalculatorTests
{
    // --- Unit Tests (jitterFactor=0 for deterministic results) ---

    [Fact]
    public void BackoffCalculator_Attempt0_Returns1Second()
    {
        var delay = BackoffCalculator.Compute(0, jitterFactor: 0);
        delay.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void BackoffCalculator_Attempt1_Returns2Seconds()
    {
        var delay = BackoffCalculator.Compute(1, jitterFactor: 0);
        delay.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void BackoffCalculator_Attempt4_Returns16Seconds()
    {
        var delay = BackoffCalculator.Compute(4, jitterFactor: 0);
        delay.Should().Be(TimeSpan.FromSeconds(16));
    }

    [Fact]
    public void BackoffCalculator_HighAttempt_CapsAt60Seconds()
    {
        var delay = BackoffCalculator.Compute(10, jitterFactor: 0);
        delay.Should().Be(TimeSpan.FromSeconds(60));
    }

    // --- Property Test: backoff delay bounds ---

    [Property(MaxTest = 20)]
    public Property BackoffCalculator_DelayIsWithinBounds()
    {
        // Validates: Requirements 1.1, 1.2, 1.6, 1.10
        var gen = Gen.Choose(0, 10);
        return Prop.ForAll(gen.ToArbitrary(), attempt =>
        {
            var delay = BackoffCalculator.Compute(attempt);
            return delay >= TimeSpan.FromSeconds(1) && delay <= TimeSpan.FromSeconds(78);
        });
    }
}
