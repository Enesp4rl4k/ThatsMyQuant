using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class ReconciliationAgentTests
{
    private readonly IExchangeService _exchangeService;
    private readonly ILogger<ReconciliationAgent> _logger;
    private readonly ReconciliationAgent _agent;

    public ReconciliationAgentTests()
    {
        _exchangeService = Substitute.For<IExchangeService>();
        _logger = Substitute.For<ILogger<ReconciliationAgent>>();
        _agent = new ReconciliationAgent(_exchangeService, _logger);
    }

    [Fact]
    public async Task VerifyIntegrityAsync_ShouldReturnInSync_WhenDriftWithinTolerance()
    {
        // Arrange — drift of 0.00005 is below the 0.0001 tolerance
        decimal localPosition = 1.00000m;
        decimal remoteBalance = 1.00005m;

        _exchangeService.GetAccountStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new Account { Balance = remoteBalance });

        // Act
        var (inSync, message) = await _agent.VerifyIntegrityAsync(localPosition);

        // Assert
        inSync.Should().BeTrue();
        message.Should().Be("States in sync.");
    }

    [Fact]
    public async Task VerifyIntegrityAsync_ShouldReturnOutOfSync_WhenDriftExceedsTolerance()
    {
        // Arrange — drift of 0.01 exceeds the 0.0001 tolerance
        decimal localPosition = 1.00m;
        decimal remoteBalance = 1.01m;

        _exchangeService.GetAccountStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new Account { Balance = remoteBalance });

        // Act
        var (inSync, message) = await _agent.VerifyIntegrityAsync(localPosition);

        // Assert
        inSync.Should().BeFalse();
        message.Should().Contain("CRITICAL");
        message.Should().Contain("desynchronized");
    }

    [Fact]
    public async Task VerifyIntegrityAsync_ShouldReturnFalse_WhenExchangeIsUnreachable()
    {
        // Arrange
        _exchangeService.GetAccountStatusAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Connection refused"));

        // Act
        var (inSync, message) = await _agent.VerifyIntegrityAsync(1.0m);

        // Assert
        inSync.Should().BeFalse();
        message.Should().Contain("Inaccessible exchange state");
    }

    [Fact]
    public async Task VerifyIntegrityAsync_ShouldReturnInSync_WhenDriftIsExactlyZero()
    {
        // Arrange — local and remote are identical
        decimal position = 5.5m;

        _exchangeService.GetAccountStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new Account { Balance = position });

        // Act
        var (inSync, message) = await _agent.VerifyIntegrityAsync(position);

        // Assert
        inSync.Should().BeTrue();
        message.Should().Be("States in sync.");
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(0, 100)]
    public async Task VerifyIntegrityAsync_ShouldReturnOutOfSync_WhenDriftIsLarge(
        decimal localPosition, decimal remoteBalance)
    {
        // Arrange
        _exchangeService.GetAccountStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new Account { Balance = remoteBalance });

        // Act
        var (inSync, message) = await _agent.VerifyIntegrityAsync(localPosition);

        // Assert
        inSync.Should().BeFalse();
        message.Should().Contain("CRITICAL");
        message.Should().Contain("desynchronized");
    }
}
