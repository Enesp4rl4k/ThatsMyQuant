using FluentAssertions;
using ThatsMyQuant.Contracts.Enums;
using Xunit;

namespace ThatsMyQuant.Tests.Strategy;

public class TradeLifecycleStateMachineTests
{
    [Theory]
    [InlineData(TradeLifecycleStatus.Pending, TradeLifecycleStatus.Submitted, true)]
    [InlineData(TradeLifecycleStatus.Pending, TradeLifecycleStatus.Cancelled, true)]
    [InlineData(TradeLifecycleStatus.Pending, TradeLifecycleStatus.Rejected, true)]
    [InlineData(TradeLifecycleStatus.Pending, TradeLifecycleStatus.Failed, true)]
    [InlineData(TradeLifecycleStatus.Submitted, TradeLifecycleStatus.Accepted, true)]
    [InlineData(TradeLifecycleStatus.Submitted, TradeLifecycleStatus.Rejected, true)]
    [InlineData(TradeLifecycleStatus.Accepted, TradeLifecycleStatus.Active, true)]
    [InlineData(TradeLifecycleStatus.Active, TradeLifecycleStatus.PartialExit, true)]
    [InlineData(TradeLifecycleStatus.Active, TradeLifecycleStatus.Closing, true)]
    [InlineData(TradeLifecycleStatus.Active, TradeLifecycleStatus.Closed, true)]
    [InlineData(TradeLifecycleStatus.PartialExit, TradeLifecycleStatus.Closed, true)]
    [InlineData(TradeLifecycleStatus.Closing, TradeLifecycleStatus.Closed, true)]
    public void CanTransitionTo_ValidTransitions_ShouldReturnTrue(
        TradeLifecycleStatus from, TradeLifecycleStatus to, bool expected)
    {
        from.CanTransitionTo(to).Should().Be(expected);
    }

    [Theory]
    [InlineData(TradeLifecycleStatus.Closed, TradeLifecycleStatus.Active)]
    [InlineData(TradeLifecycleStatus.Closed, TradeLifecycleStatus.Pending)]
    [InlineData(TradeLifecycleStatus.Failed, TradeLifecycleStatus.Submitted)]
    [InlineData(TradeLifecycleStatus.Rejected, TradeLifecycleStatus.Accepted)]
    [InlineData(TradeLifecycleStatus.Cancelled, TradeLifecycleStatus.Active)]
    [InlineData(TradeLifecycleStatus.Active, TradeLifecycleStatus.Pending)]
    [InlineData(TradeLifecycleStatus.Active, TradeLifecycleStatus.Submitted)]
    [InlineData(TradeLifecycleStatus.Closing, TradeLifecycleStatus.Active)]
    [InlineData(TradeLifecycleStatus.PartialExit, TradeLifecycleStatus.Accepted)]
    public void CanTransitionTo_InvalidTransitions_ShouldReturnFalse(
        TradeLifecycleStatus from, TradeLifecycleStatus to)
    {
        from.CanTransitionTo(to).Should().BeFalse(
            $"transition from {from} to {to} should be invalid");
    }

    [Theory]
    [InlineData(TradeLifecycleStatus.Closed)]
    [InlineData(TradeLifecycleStatus.Cancelled)]
    [InlineData(TradeLifecycleStatus.Rejected)]
    [InlineData(TradeLifecycleStatus.Failed)]
    public void IsTerminal_TerminalStates_ShouldReturnTrue(TradeLifecycleStatus status)
    {
        status.IsTerminal().Should().BeTrue();
    }

    [Theory]
    [InlineData(TradeLifecycleStatus.Pending)]
    [InlineData(TradeLifecycleStatus.Submitted)]
    [InlineData(TradeLifecycleStatus.Accepted)]
    [InlineData(TradeLifecycleStatus.PartiallyFilled)]
    [InlineData(TradeLifecycleStatus.Active)]
    [InlineData(TradeLifecycleStatus.PartialExit)]
    [InlineData(TradeLifecycleStatus.Closing)]
    public void IsTerminal_NonTerminalStates_ShouldReturnFalse(TradeLifecycleStatus status)
    {
        status.IsTerminal().Should().BeFalse();
    }

    [Fact]
    public void CanTransitionTo_SameState_ShouldReturnTrue()
    {
        TradeLifecycleStatus.Active.CanTransitionTo(TradeLifecycleStatus.Active)
            .Should().BeTrue("self-transition should always be allowed");
    }

    [Fact]
    public void TerminalStates_CannotTransitionToAnything()
    {
        var terminalStates = new[]
        {
            TradeLifecycleStatus.Closed,
            TradeLifecycleStatus.Cancelled,
            TradeLifecycleStatus.Rejected,
            TradeLifecycleStatus.Failed
        };

        var allStates = Enum.GetValues<TradeLifecycleStatus>();

        foreach (var terminal in terminalStates)
        {
            foreach (var target in allStates)
            {
                if (terminal == target) continue; // Self-transition is OK
                terminal.CanTransitionTo(target).Should().BeFalse(
                    $"terminal state {terminal} should not transition to {target}");
            }
        }
    }

    [Theory]
    [InlineData(TradeStatus.Pending, TradeLifecycleStatus.Pending)]
    [InlineData(TradeStatus.Queued, TradeLifecycleStatus.Submitted)]
    [InlineData(TradeStatus.Executed, TradeLifecycleStatus.Active)]
    [InlineData(TradeStatus.Rejected, TradeLifecycleStatus.Rejected)]
    [InlineData(TradeStatus.Failed, TradeLifecycleStatus.Failed)]
    [InlineData(TradeStatus.DeadLetter, TradeLifecycleStatus.Failed)]
    public void ToLifecycleStatus_ShouldMapCorrectly(TradeStatus input, TradeLifecycleStatus expected)
    {
        input.ToLifecycleStatus().Should().Be(expected);
    }
}
