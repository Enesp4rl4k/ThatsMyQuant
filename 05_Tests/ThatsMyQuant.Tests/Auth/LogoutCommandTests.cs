using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Dashboard.Auth;
using Xunit;

namespace ThatsMyQuant.Tests.Auth;

// Feature: session-logout, Property 5
// Feature: session-logout, Property 6
// Validates: Requirements 7.1, 7.7

/// <summary>
/// Property-based tests for logout idempotence and no-op-when-null behaviour.
/// Rather than constructing a full <see cref="MainViewModel"/> (which requires many WPF
/// dependencies), these tests exercise the core logout logic directly through
/// <see cref="CurrentSession"/>, which is exactly what <c>LogoutCommand</c> delegates to.
/// </summary>
public class LogoutCommandTests : IDisposable
{
    public LogoutCommandTests() => CurrentSession.Clear();
    public void Dispose()        => CurrentSession.Clear();

    // ── helpers ───────────────────────────────────────────────────────────────

    private static Gen<OperatorUser> UserGen() =>
        from username in Gen.Elements("alice", "bob", "charlie", "dave", "eve")
        from role     in Gen.Elements(Role.Admin, Role.Operator, Role.ReadOnly)
        select new OperatorUser
        {
            Username = username,
            Email    = $"{username}@test.com",
            Role     = role,
            IsActive = true
        };

    /// <summary>
    /// Simulates the core of <c>LogoutCommand</c>: guard + Clear.
    /// </summary>
    private static void SimulateLogout()
    {
        if (CurrentSession.Current is null)
            return;
        CurrentSession.Clear();
    }

    // ── Property 5: Idempotence ───────────────────────────────────────────────

    /// <summary>
    /// Property 5: Idempotence — for all sequences of logout executions,
    /// <see cref="CurrentSession.Current"/> is null after each call.
    /// Validates: Requirements 1.2, 7.1
    /// </summary>
    [Property(MaxTest = 100)]
    public Property P5_Logout_AfterEachCall_CurrentSessionIsNull()
    {
        // Generate: a user to start with, and a repeat count (1..5)
        var gen =
            from user  in UserGen()
            from extra in Gen.Choose(1, 5)
            select (user, extra);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (user, extra) = tuple;

            // Set a session, then call logout once
            CurrentSession.Current = user;
            SimulateLogout();
            if (CurrentSession.Current is not null) return false;

            // Call logout extra more times — must remain null each time
            for (int i = 0; i < extra; i++)
            {
                SimulateLogout();
                if (CurrentSession.Current is not null) return false;
            }

            return true;
        });
    }

    // ── Property 6: No-op when null ───────────────────────────────────────────

    /// <summary>
    /// Property 6: No-op when null — calling logout when
    /// <see cref="CurrentSession.Current"/> is null does not throw and leaves
    /// the session null.
    /// Validates: Requirements 7.7
    /// </summary>
    [Property(MaxTest = 100)]
    public Property P6_Logout_WhenSessionIsNull_DoesNotThrow()
    {
        return Prop.ForAll(Gen.Choose(1, 10).ToArbitrary(), callCount =>
        {
            // Ensure session is null
            CurrentSession.Clear();

            Exception? thrown = null;
            try
            {
                for (int i = 0; i < callCount; i++)
                    SimulateLogout();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            return thrown is null && CurrentSession.Current is null;
        });
    }
}
