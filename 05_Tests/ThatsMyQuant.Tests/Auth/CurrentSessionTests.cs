using FluentAssertions;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Dashboard.Auth;
using Xunit;

namespace ThatsMyQuant.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="CurrentSession"/>.
/// Validates: Requirements 6.1, 6.3
/// </summary>
public class CurrentSessionTests : IDisposable
{
    // Reset static state before and after each test for isolation
    public CurrentSessionTests() => CurrentSession.Clear();
    public void Dispose()        => CurrentSession.Clear();

    // ── 6.1: session holds the authenticated user ─────────────────────────

    [Fact]
    public void Current_BeforeAnyAssignment_IsNull()
    {
        CurrentSession.Current.Should().BeNull();
    }

    [Fact]
    public void Current_AfterAssigningUser_IsNotNull()
    {
        var user = new OperatorUser { Username = "alice", Email = "alice@test.com", Role = Role.Operator };

        CurrentSession.Current = user;

        CurrentSession.Current.Should().NotBeNull();
        CurrentSession.Current.Should().BeSameAs(user);
    }

    // ── 6.3: Clear() removes the session ─────────────────────────────────

    [Fact]
    public void Clear_AfterAssigningUser_SetsCurrentToNull()
    {
        var user = new OperatorUser { Username = "bob", Email = "bob@test.com", Role = Role.Admin };
        CurrentSession.Current = user;

        CurrentSession.Clear();

        CurrentSession.Current.Should().BeNull();
    }

    [Fact]
    public void Clear_WhenAlreadyNull_DoesNotThrow()
    {
        var act = () => CurrentSession.Clear();

        act.Should().NotThrow();
        CurrentSession.Current.Should().BeNull();
    }

    [Fact]
    public void Current_CanBeReassigned_ReflectsLatestUser()
    {
        var user1 = new OperatorUser { Username = "user1", Email = "u1@test.com", Role = Role.Operator };
        var user2 = new OperatorUser { Username = "user2", Email = "u2@test.com", Role = Role.ReadOnly };

        CurrentSession.Current = user1;
        CurrentSession.Current = user2;

        CurrentSession.Current.Should().BeSameAs(user2);
    }
}
