using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Structural;

/// <summary>
/// Sprint 1 Preservation Tests.
/// These tests PASS on unfixed code — they assert that CheckOrderAllowed() semantics
/// are correct and must remain unchanged after the IPortfolioManager interface extraction in Task 9.
/// Validates: Requirements 3.2, 3.3
/// </summary>
public class Sprint1PreservationTests
{
    private readonly ILogger<CircuitBreakerService> _logger = Substitute.For<ILogger<CircuitBreakerService>>();

    /// <summary>
    /// P1: CheckOrderAllowed() returns (true, null) when drawdown is below threshold and no halt is active.
    /// Validates: Requirements 3.2, 3.3
    /// </summary>
    [Fact]
    public void P1_CheckOrderAllowed_ReturnsAllowed_WhenDrawdownBelowThresholdAndNoHalt()
    {
        var portfolio = new MemoryPortfolioManager();
        portfolio.SetBalance("USDT", 100_000m); // sets peak equity
        var service = new CircuitBreakerService(_logger, portfolio, maxDrawdownPercent: 5m, maxOrdersPerSecond: 100);

        var result = service.CheckOrderAllowed();

        result.Allowed.Should().BeTrue(
            because: "drawdown is 0% which is below the 5% threshold and no halt is active");
        result.BlockReason.Should().BeNull(
            because: "no block reason should be present when the order is allowed");
    }

    /// <summary>
    /// P2: CheckOrderAllowed() returns (false, non-null reason) when drawdown exceeds threshold.
    /// Sets peak at 100_000 then drops to 94_000 — drawdown = 6% which exceeds the 5% threshold.
    /// Validates: Requirements 3.2, 3.3
    /// </summary>
    [Fact]
    public void P2_CheckOrderAllowed_ReturnsBlocked_WhenDrawdownExceedsThreshold()
    {
        var portfolio = new MemoryPortfolioManager();
        portfolio.SetBalance("USDT", 100_000m); // establishes peak equity
        portfolio.SetBalance("USDT", 94_000m);  // drops balance — drawdown = 6%
        var service = new CircuitBreakerService(_logger, portfolio, maxDrawdownPercent: 5m);

        var result = service.CheckOrderAllowed();

        result.Allowed.Should().BeFalse(
            because: "drawdown is 6% which exceeds the 5% threshold");
        result.BlockReason.Should().NotBeNull(
            because: "a block reason must be provided when the order is rejected");
        result.BlockReason.Should().Contain("Drawdown",
            because: "the block reason should identify the drawdown circuit breaker as the cause");
    }
}
