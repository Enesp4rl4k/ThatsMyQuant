using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Interfaces;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class CircuitBreakerServiceTests
{
    private readonly ILogger<CircuitBreakerService> _logger = Substitute.For<ILogger<CircuitBreakerService>>();

    [Fact]
    public void CheckOrderAllowed_ShouldBlockWhenManuallyHalted()
    {
        var portfolio = Substitute.For<IPortfolioManager>();
        portfolio.GetDrawdownPercent().Returns(0m);
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
        // Uses a real MemoryPortfolioManager to exercise actual drawdown calculation
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
        var portfolio = Substitute.For<IPortfolioManager>();
        portfolio.GetDrawdownPercent().Returns(0m);
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
        var portfolio = Substitute.For<IPortfolioManager>();
        portfolio.GetDrawdownPercent().Returns(0m);
        var service = new CircuitBreakerService(_logger, portfolio, maxOrdersPerSecond: 2);

        service.CheckOrderAllowed().Allowed.Should().BeTrue();
        service.CheckOrderAllowed().Allowed.Should().BeTrue();

        var result = service.CheckOrderAllowed();

        result.Allowed.Should().BeFalse();
        result.BlockReason.Should().Contain("Rate limited");
        service.TotalRateLimitHits.Should().Be(1);
    }
}
