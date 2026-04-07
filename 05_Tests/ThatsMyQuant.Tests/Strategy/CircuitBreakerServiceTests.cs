using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class CircuitBreakerServiceTests
{
    private readonly ILogger<CircuitBreakerService> _logger = Substitute.For<ILogger<CircuitBreakerService>>();

    [Fact]
    public void CheckOrderAllowed_ShouldBlockWhenManuallyHalted()
    {
        var portfolio = new MemoryPortfolioManager();
        portfolio.SetBalance("USDT", 100_000m);
        var service = new CircuitBreakerService(_logger, portfolio);

        service.Halt("Operator halt");
        var result = service.CheckOrderAllowed();

        result.Allowed.Should().BeFalse();
        result.BlockReason.Should().Contain("HALTED");
        result.BlockReason.Should().Contain("Operator halt");
        service.TotalOrdersBlocked.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CheckOrderAllowed_ShouldHaltWhenDrawdownThresholdBreached()
    {
        var portfolio = new MemoryPortfolioManager();
        portfolio.SetBalance("USDT", 100_000m);
        portfolio.SetBalance("USDT", 94_000m);
        var service = new CircuitBreakerService(_logger, portfolio, maxDrawdownPercent: 5m);

        var result = service.CheckOrderAllowed();

        result.Allowed.Should().BeFalse();
        service.IsHalted.Should().BeTrue();
        service.HaltReason.Should().Contain("Drawdown breaker triggered");
    }

    [Fact]
    public void RecordError_ShouldTriggerErrorCascadeHalt()
    {
        var portfolio = new MemoryPortfolioManager();
        portfolio.SetBalance("USDT", 100_000m);
        var service = new CircuitBreakerService(_logger, portfolio, maxConsecutiveErrors: 3);

        service.RecordError(new InvalidOperationException("first"));
        service.RecordError(new InvalidOperationException("second"));
        service.RecordError(new InvalidOperationException("third"));

        service.IsHalted.Should().BeTrue();
        service.HaltReason.Should().Contain("Error cascade halt");
        service.HaltReason.Should().Contain("third");
    }

    [Fact]
    public void CheckOrderAllowed_ShouldRateLimitAfterConfiguredThreshold()
    {
        var portfolio = new MemoryPortfolioManager();
        portfolio.SetBalance("USDT", 100_000m);
        var service = new CircuitBreakerService(_logger, portfolio, maxOrdersPerSecond: 2);

        service.CheckOrderAllowed().Allowed.Should().BeTrue();
        service.CheckOrderAllowed().Allowed.Should().BeTrue();

        var result = service.CheckOrderAllowed();

        result.Allowed.Should().BeFalse();
        result.BlockReason.Should().Contain("Rate limited");
        service.TotalRateLimitHits.Should().Be(1);
    }

    [Fact]
    public void Resume_ShouldClearHaltAndResetErrors()
    {
        var portfolio = new MemoryPortfolioManager();
        portfolio.SetBalance("USDT", 100_000m);
        var service = new CircuitBreakerService(_logger, portfolio);

        service.Halt("Test halt");
        service.IsHalted.Should().BeTrue();

        service.Resume();

        service.IsHalted.Should().BeFalse();
        service.HaltReason.Should().BeEmpty();
        service.CheckOrderAllowed().Allowed.Should().BeTrue();
    }

    [Fact]
    public void RecordSuccess_ShouldResetConsecutiveErrorCounter()
    {
        var portfolio = new MemoryPortfolioManager();
        portfolio.SetBalance("USDT", 100_000m);
        var service = new CircuitBreakerService(_logger, portfolio, maxConsecutiveErrors: 3);

        service.RecordError(new Exception("err1"));
        service.RecordError(new Exception("err2"));
        service.RecordSuccess(); // Should reset counter
        service.RecordError(new Exception("err3"));

        service.IsHalted.Should().BeFalse();
    }

    [Fact]
    public void GetStatus_ShouldReturnAccurateSnapshot()
    {
        var portfolio = new MemoryPortfolioManager();
        portfolio.SetBalance("USDT", 100_000m);
        var service = new CircuitBreakerService(_logger, portfolio, maxDrawdownPercent: 5m, maxConsecutiveErrors: 3, maxOrdersPerSecond: 10);

        var status = service.GetStatus();

        status.IsHalted.Should().BeFalse();
        status.HaltReason.Should().BeEmpty();
        status.MaxDrawdownThreshold.Should().Be(5m);
        status.MaxConsecutiveErrorThreshold.Should().Be(3);
        status.MaxOrdersPerSecond.Should().Be(10);
        status.CurrentDrawdownPercent.Should().Be(0m);
    }

    [Fact]
    public void GetStatus_AfterHalt_ShouldReflectHaltedState()
    {
        var portfolio = new MemoryPortfolioManager();
        portfolio.SetBalance("USDT", 100_000m);
        var service = new CircuitBreakerService(_logger, portfolio);

        service.Halt("Emergency stop");
        var status = service.GetStatus();

        status.IsHalted.Should().BeTrue();
        status.HaltReason.Should().Be("Emergency stop");
        status.TotalOrdersBlocked.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CheckOrderAllowed_ShouldAllowWhenDrawdownBelowThreshold()
    {
        var portfolio = new MemoryPortfolioManager();
        portfolio.SetBalance("USDT", 100_000m);
        portfolio.SetBalance("USDT", 96_000m); // 4% drawdown < 5% threshold
        var service = new CircuitBreakerService(_logger, portfolio, maxDrawdownPercent: 5m);

        var result = service.CheckOrderAllowed();

        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Halt_ShouldBlockSubsequentOrders()
    {
        var portfolio = new MemoryPortfolioManager();
        portfolio.SetBalance("USDT", 100_000m);
        var service = new CircuitBreakerService(_logger, portfolio);

        service.Halt("Test halt reason");

        var result1 = service.CheckOrderAllowed();
        var result2 = service.CheckOrderAllowed();

        result1.Allowed.Should().BeFalse();
        result2.Allowed.Should().BeFalse();
        service.TotalOrdersBlocked.Should().BeGreaterOrEqualTo(2);
    }
}
