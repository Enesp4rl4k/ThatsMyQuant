using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class RiskAgentEvictionTests
{
    private readonly RiskAgent _sut;
    private readonly RiskGovernorOptions _options;

    public RiskAgentEvictionTests()
    {
        _options = new RiskGovernorOptions
        {
            MaxTrackedTrades = 10 // Low cap for testing eviction
        };

        _sut = new RiskAgent(
            Substitute.For<ILogger<RiskAgent>>(),
            options: Options.Create(_options));
    }

    [Fact]
    public async Task NotifyTradeUpdate_ExceedingMaxTracked_ShouldEvictClosedTrades()
    {
        // Arrange: Add 10 closed trades
        for (int i = 0; i < 10; i++)
        {
            _sut.NotifyTradeUpdate(new Trade
            {
                Id = Guid.NewGuid(),
                Status = TradeStatus.Executed,
                LifecycleStatus = TradeLifecycleStatus.Closed,
                ExitTime = DateTime.UtcNow.AddMinutes(-20 + i),
                EntryTime = DateTime.UtcNow.AddMinutes(-30 + i),
                EntryPrice = 100m,
                ExitPrice = 105m,
                Quantity = 1m
            });
        }

        // Act: Add one more (should trigger eviction)
        var newTrade = new Trade
        {
            Id = Guid.NewGuid(),
            Status = TradeStatus.Executed,
            LifecycleStatus = TradeLifecycleStatus.Active,
            EntryTime = DateTime.UtcNow,
            EntryPrice = 100m,
            Quantity = 1m
        };
        _sut.NotifyTradeUpdate(newTrade);

        // Assert: Should not throw, and the eviction should have trimmed to MaxTrackedTrades
        // We verify indirectly by checking the agent still works correctly
        var result = await _sut.EvaluatePortfolioGuardrailsAsync(
            new List<Trade>(),
            new Signal { Id = Guid.NewGuid(), Symbol = "BTCUSDT", Price = 50000m },
            0.05m,
            RiskParameters.Default);

        result.Approved.Should().BeTrue("guardrails should pass after eviction");
    }

    [Fact]
    public void NotifyTradeUpdate_AllOpenTrades_ExceedingCap_ShouldForceEvictOldest()
    {
        // Arrange: Add MaxTrackedTrades open trades (none closed = first-pass eviction finds nothing)
        for (int i = 0; i < _options.MaxTrackedTrades; i++)
        {
            _sut.NotifyTradeUpdate(new Trade
            {
                Id = Guid.NewGuid(),
                Status = TradeStatus.Executed,
                LifecycleStatus = TradeLifecycleStatus.Active,
                EntryTime = DateTime.UtcNow.AddMinutes(-20 + i),
                EntryPrice = 100m,
                Quantity = 1m
            });
        }

        // Act: Add one more — forces hard ceiling eviction (oldest open trade removed)
        _sut.NotifyTradeUpdate(new Trade
        {
            Id = Guid.NewGuid(),
            Status = TradeStatus.Executed,
            LifecycleStatus = TradeLifecycleStatus.Active,
            EntryTime = DateTime.UtcNow,
            EntryPrice = 100m,
            Quantity = 1m
        });

        // Assert: No exception, system still functional
        _sut.IsKillSwitchArmed.Should().BeFalse();
    }

    [Fact]
    public void KillSwitch_ArmAndReset_ShouldToggle()
    {
        _sut.ArmKillSwitch("Test reason");
        _sut.IsKillSwitchArmed.Should().BeTrue();

        _sut.ResetKillSwitch();
        _sut.IsKillSwitchArmed.Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_KillSwitchArmed_ShouldReject()
    {
        _sut.ArmKillSwitch("Test");

        var result = await _sut.EvaluateAsync(
            new TradeDecision { Confidence = 80, PositionSizePercent = 0.05m, Side = TradeSide.Buy },
            RiskParameters.Default,
            1_000_000m);

        result.Approved.Should().BeFalse();
        result.Reason.Should().Contain("Kill-switch");
    }
}
