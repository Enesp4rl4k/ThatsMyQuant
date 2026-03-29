using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public sealed class ReconciliationAgentTests
{
    private readonly IExchangeService _exchange = Substitute.For<IExchangeService>();
    private readonly ILogger<ReconciliationAgent> _logger = Substitute.For<ILogger<ReconciliationAgent>>();

    private ReconciliationAgent CreateAgent() => new(_exchange, _logger);

    // ─── In-sync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyIntegrity_ReturnsInSync_WhenPositionsMatch()
    {
        _exchange.GetAccountStatusAsync().Returns(new Account { Balance = 0.5m });

        var agent = CreateAgent();
        var (inSync, message) = await agent.VerifyIntegrityAsync(0.5m);

        inSync.Should().BeTrue();
        message.Should().Contain("sync");
    }

    [Fact]
    public async Task VerifyIntegrity_ReturnsInSync_WhenDriftWithinTolerance()
    {
        // Tolerance is 0.0001 (0.01%)
        _exchange.GetAccountStatusAsync().Returns(new Account { Balance = 0.50005m });

        var agent = CreateAgent();
        var (inSync, _) = await agent.VerifyIntegrityAsync(0.5001m);

        inSync.Should().BeTrue();
    }

    // ─── Drift detected ──────────────────────────────────────────────────

    [Fact]
    public async Task VerifyIntegrity_DetectsDrift_WhenPositionsDiverge()
    {
        _exchange.GetAccountStatusAsync().Returns(new Account { Balance = 10m });

        var agent = CreateAgent();
        var (inSync, message) = await agent.VerifyIntegrityAsync(0.5m);

        inSync.Should().BeFalse();
        message.Should().Contain("desynchronized");
    }

    [Fact]
    public async Task VerifyIntegrity_DetectsDrift_WhenLocalIsZero()
    {
        _exchange.GetAccountStatusAsync().Returns(new Account { Balance = 1.0m });

        var agent = CreateAgent();
        var (inSync, _) = await agent.VerifyIntegrityAsync(0m);

        inSync.Should().BeFalse();
    }

    // ─── Exchange unreachable ────────────────────────────────────────────

    [Fact]
    public async Task VerifyIntegrity_ReturnsUnsafe_WhenExchangeThrows()
    {
        _exchange.GetAccountStatusAsync().Throws(new HttpRequestException("Connection refused"));

        var agent = CreateAgent();
        var (inSync, message) = await agent.VerifyIntegrityAsync(0.5m);

        inSync.Should().BeFalse();
        message.Should().Contain("Inaccessible");
    }

    [Fact]
    public async Task VerifyIntegrity_ReturnsUnsafe_WhenExchangeTimesOut()
    {
        _exchange.GetAccountStatusAsync().Throws(new TaskCanceledException("Timeout"));

        var agent = CreateAgent();
        var (inSync, message) = await agent.VerifyIntegrityAsync(0.5m);

        inSync.Should().BeFalse();
    }

    // ─── Edge cases ──────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyIntegrity_HandlesNegativeLocalPosition()
    {
        _exchange.GetAccountStatusAsync().Returns(new Account { Balance = 0m });

        var agent = CreateAgent();
        var (inSync, _) = await agent.VerifyIntegrityAsync(-1.0m);

        inSync.Should().BeFalse();
    }
}
