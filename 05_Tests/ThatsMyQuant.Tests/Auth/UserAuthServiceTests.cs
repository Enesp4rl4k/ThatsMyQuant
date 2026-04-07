using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Dashboard.Auth;
using ThatsMyQuant.Infrastructure.Persistence;
using Xunit;

namespace ThatsMyQuant.Tests.Auth;

public class UserAuthServiceTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static (SqliteConnection connection, QuantDbContext db) CreateDb()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<QuantDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new QuantDbContext(options);
        db.Database.EnsureCreated();
        return (connection, db);
    }

    private static (SqliteConnection connection, QuantDbContext db, UserAuthService svc) CreateSeeded()
    {
        var (connection, db) = CreateDb();
        var svc = new UserAuthService(db);
        svc.SeedAsync().GetAwaiter().GetResult();
        return (connection, db, svc);
    }

    // ── character sets for generators ────────────────────────────────────────

    private static readonly char[] Uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();
    private static readonly char[] Lowercase = "abcdefghijklmnopqrstuvwxyz".ToCharArray();
    private static readonly char[] Digits    = "0123456789".ToCharArray();
    private static readonly char[] Specials  = "!@#$%^&*()_+-=[]{};\':\",./<>?".ToCharArray();

    private static Gen<char> PickChar(char[] pool) => Gen.Elements<char>(pool);

    private static Gen<string> StringOfLen(int len, char[] pool) =>
        Gen.ArrayOf<char>(PickChar(pool), len).Select(cs => new string(cs));

    /// <summary>Generates a valid password satisfying all complexity rules.</summary>
    private static Gen<string> ValidPasswordGen() =>
        from lowerLen in Gen.Choose(0, 10)
        from lowerPad in StringOfLen(lowerLen, Lowercase)
        from upper    in PickChar(Uppercase)
        from digit    in PickChar(Digits)
        from special  in PickChar(Specials)
        let core   = lowerPad + upper + digit + special
        let padded = core.Length >= 8 ? core : core + new string('a', 8 - core.Length)
        select padded;

    /// <summary>Generates an invalid password (violates at least one complexity rule).</summary>
    private static Gen<string> InvalidPasswordGen()
    {
        var tooShort = from len in Gen.Choose(1, 7)
                       from s in StringOfLen(len, Uppercase.Concat(Lowercase).Concat(Digits).Concat(Specials).ToArray())
                       select s;

        var noUppercase = from len in Gen.Choose(8, 20)
                          from s in StringOfLen(len, Lowercase.Concat(Digits).Concat(Specials).ToArray())
                          select s;

        var noDigit = from len in Gen.Choose(7, 19)
                      from s in StringOfLen(len, Uppercase.Concat(Lowercase).Concat(Specials).ToArray())
                      select "A" + s;

        var noSpecial = from len in Gen.Choose(6, 18)
                        from s in StringOfLen(len, Uppercase.Concat(Lowercase).Concat(Digits).ToArray())
                        select "A1" + s;

        return Gen.OneOf(tooShort, noUppercase, noDigit, noSpecial);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Task 6.2 — P5: authentication succeeds only for active users with correct credentials
    // Feature: multi-user-auth, Property 5: authentication succeeds only for active users with correct credentials
    // Validates: Requirements 4.1, 4.2, 4.3, 4.4
    // ─────────────────────────────────────────────────────────────────────────

    [Property(MaxTest = 100)]
    public Property P5_RandomCredentials_NotMatchingAnyUser_ReturnsNull()
    {
        var gen =
            from emailLocal in StringOfLen(6, Lowercase)
            from password   in ValidPasswordGen()
            select (email: emailLocal + "@random.test", password);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (email, password) = tuple;
            var (connection, db, svc) = CreateSeeded();
            try
            {
                // Skip if the random email accidentally matches the seeded admin
                if (email.Equals("admin@thatsmyquant.com", StringComparison.OrdinalIgnoreCase))
                    return true;

                var result = svc.AuthenticateAsync(email, password).GetAwaiter().GetResult();
                return result == null;
            }
            finally
            {
                db.Dispose();
                connection.Dispose();
            }
        });
    }

    [Property(MaxTest = 100)]
    public Property P5_InactiveUser_ReturnsNull_RegardlessOfPassword()
    {
        return Prop.ForAll(ValidPasswordGen().ToArbitrary(), password =>
        {
            var (connection, db) = CreateDb();
            try
            {
                var svc = new UserAuthService(db);
                const string knownPassword = "KnownPass1!";

                var user = new OperatorUser
                {
                    Username     = "inactive_user",
                    Email        = "inactive@test.com",
                    PasswordHash = PasswordHasher.Hash(knownPassword),
                    Role         = Role.Operator,
                    IsActive     = false
                };
                db.OperatorUsers.Add(user);
                db.SaveChanges();

                // Correct password — must return null because inactive
                var result1 = svc.AuthenticateAsync("inactive@test.com", knownPassword).GetAwaiter().GetResult();
                if (result1 != null) return false;

                // Random password — must also return null
                var result2 = svc.AuthenticateAsync("inactive@test.com", password).GetAwaiter().GetResult();
                return result2 == null;
            }
            finally
            {
                db.Dispose();
                connection.Dispose();
            }
        });
    }

    [Property(MaxTest = 100)]
    public Property P5_ActiveUser_CorrectCredentials_ReturnsUserAndUpdatesLastLoginAt()
    {
        return Prop.ForAll(ValidPasswordGen().ToArbitrary(), password =>
        {
            var (connection, db) = CreateDb();
            try
            {
                var svc = new UserAuthService(db);

                var user = new OperatorUser
                {
                    Username     = "active_user",
                    Email        = "active@test.com",
                    PasswordHash = PasswordHasher.Hash(password),
                    Role         = Role.Operator,
                    IsActive     = true
                };
                db.OperatorUsers.Add(user);
                db.SaveChanges();

                var before = DateTime.UtcNow;
                var result = svc.AuthenticateAsync("active@test.com", password).GetAwaiter().GetResult();

                if (result == null) return false;
                if (result.Id != user.Id) return false;
                if (result.LastLoginAt == null) return false;
                if (result.LastLoginAt < before) return false;

                return true;
            }
            finally
            {
                db.Dispose();
                connection.Dispose();
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Task 6.3 — P8: seed is idempotent
    // Feature: multi-user-auth, Property 8: seed is idempotent
    // Validates: Requirements 2.2
    // ─────────────────────────────────────────────────────────────────────────

    [Property(MaxTest = 100)]
    public Property P8_SeedAsync_IsIdempotent()
    {
        // N = number of extra SeedAsync calls after the first (N in [2, 5])
        return Prop.ForAll(Gen.Choose(2, 5).ToArbitrary(), n =>
        {
            var (connection, db) = CreateDb();
            try
            {
                var svc = new UserAuthService(db);

                // First seed — pre-populates with at least one user
                svc.SeedAsync().GetAwaiter().GetResult();
                var countAfterFirst = db.OperatorUsers.Count();

                // Call SeedAsync N more times; count must not change
                for (int i = 0; i < n; i++)
                {
                    svc.SeedAsync().GetAwaiter().GetResult();
                    var countNow = db.OperatorUsers.Count();
                    if (countNow != countAfterFirst) return false;
                }

                return true;
            }
            finally
            {
                db.Dispose();
                connection.Dispose();
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Task 6.4 — P13: service throws on invalid password
    // Feature: multi-user-auth, Property 13: service methods throw on invalid password
    // Validates: Requirements 10.6, 10.7
    // ─────────────────────────────────────────────────────────────────────────

    [Property(MaxTest = 100)]
    public Property P13_CreateUserAsync_InvalidPassword_ThrowsArgumentException()
    {
        return Prop.ForAll(InvalidPasswordGen().ToArbitrary(), invalidPassword =>
        {
            var (connection, db) = CreateDb();
            try
            {
                var svc = new UserAuthService(db);
                var expectedError = PasswordValidator.Validate(invalidPassword);

                ArgumentException? thrown = null;
                try
                {
                    svc.CreateUserAsync("testuser", "test@test.com", invalidPassword, Role.Operator)
                       .GetAwaiter().GetResult();
                }
                catch (ArgumentException ex)
                {
                    thrown = ex;
                }

                if (thrown == null) return false;
                if (!thrown.Message.Contains(expectedError!)) return false;

                // No row should have been inserted
                return db.OperatorUsers.Count() == 0;
            }
            finally
            {
                db.Dispose();
                connection.Dispose();
            }
        });
    }

    [Property(MaxTest = 100)]
    public Property P13_UpdatePasswordAsync_InvalidPassword_ThrowsArgumentException()
    {
        return Prop.ForAll(InvalidPasswordGen().ToArbitrary(), invalidPassword =>
        {
            var (connection, db) = CreateDb();
            try
            {
                var svc = new UserAuthService(db);

                // Insert a user with a valid password first
                const string validPassword = "ValidPass1!";
                var user = new OperatorUser
                {
                    Username     = "update_test",
                    Email        = "update@test.com",
                    PasswordHash = PasswordHasher.Hash(validPassword),
                    Role         = Role.Operator,
                    IsActive     = true
                };
                db.OperatorUsers.Add(user);
                db.SaveChanges();

                var originalHash = user.PasswordHash;
                var expectedError = PasswordValidator.Validate(invalidPassword);

                ArgumentException? thrown = null;
                try
                {
                    svc.UpdatePasswordAsync(user.Id, invalidPassword).GetAwaiter().GetResult();
                }
                catch (ArgumentException ex)
                {
                    thrown = ex;
                }

                if (thrown == null) return false;
                if (!thrown.Message.Contains(expectedError!)) return false;

                // Hash must be unchanged
                db.Entry(user).Reload();
                return user.PasswordHash == originalHash;
            }
            finally
            {
                db.Dispose();
                connection.Dispose();
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Task 6.5 — Unit tests for UserAuthService
    // Validates: Requirements 2.1, 4.1, 9.4, 9.5, 9.6
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeedAsync_OnEmptyDb_CreatesExactlyOneAdmin_WithExpectedFields()
    {
        var (connection, db) = CreateDb();
        try
        {
            var svc = new UserAuthService(db);
            await svc.SeedAsync();

            var users = await db.OperatorUsers.ToListAsync();
            users.Should().HaveCount(1);

            var admin = users[0];
            admin.Username.Should().Be("admin");
            admin.Role.Should().Be(Role.Admin);
            admin.IsActive.Should().BeTrue();
            admin.MustChangePassword.Should().BeFalse();
        }
        finally
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task AuthenticateAsync_WithSeededAdminCredentials_ReturnsUser()
    {
        var (connection, db, svc) = CreateSeeded();
        try
        {
            var result = await svc.AuthenticateAsync("admin@thatsmyquant.com", "Admin@123!");

            result.Should().NotBeNull();
            result!.Username.Should().Be("admin");
            result.Role.Should().Be(Role.Admin);
        }
        finally
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task SeedAsync_WithExistingDefaultAdmin_RepairsCredentialsAndReactivatesAccount()
    {
        var (connection, db) = CreateDb();
        try
        {
            db.OperatorUsers.Add(new OperatorUser
            {
                Username = "legacy-admin",
                Email = "admin@thatsmyquant.com",
                PasswordHash = PasswordHasher.Hash("WrongPass1!"),
                Role = Role.ReadOnly,
                IsActive = false,
                MustChangePassword = false
            });
            await db.SaveChangesAsync();

            var svc = new UserAuthService(db);
            await svc.SeedAsync();

            var admin = await db.OperatorUsers.SingleAsync(u => u.Email == "admin@thatsmyquant.com");
            admin.Username.Should().Be("admin");
            admin.Role.Should().Be(Role.Admin);
            admin.IsActive.Should().BeTrue();
            admin.MustChangePassword.Should().BeFalse();
            PasswordHasher.Verify("Admin@123!", admin.PasswordHash).Should().BeTrue();
        }
        finally
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateUserAsync_WithValidPassword_CreatesUserRetrievableViaListUsers()
    {
        var (connection, db) = CreateDb();
        try
        {
            var svc = new UserAuthService(db);

            var created = await svc.CreateUserAsync("newuser", "newuser@test.com", "NewPass1!", Role.Operator);

            var users = await svc.ListUsersAsync();
            users.Should().ContainSingle(u => u.Id == created.Id);
            users.Single(u => u.Id == created.Id).Username.Should().Be("newuser");
        }
        finally
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task SetUserActiveAsync_LastActiveAdmin_ThrowsInvalidOperationException()
    {
        var (connection, db, svc) = CreateSeeded();
        try
        {
            var admin = await db.OperatorUsers.SingleAsync(u => u.Role == Role.Admin);

            var act = async () => await svc.SetUserActiveAsync(admin.Id, false);

            await act.Should().ThrowAsync<InvalidOperationException>();

            // DB must be unchanged
            await db.Entry(admin).ReloadAsync();
            admin.IsActive.Should().BeTrue();
        }
        finally
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task DeleteUserAsync_LastActiveAdmin_ThrowsInvalidOperationException()
    {
        var (connection, db, svc) = CreateSeeded();
        try
        {
            var admin = await db.OperatorUsers.SingleAsync(u => u.Role == Role.Admin);

            var act = async () => await svc.DeleteUserAsync(admin.Id);

            await act.Should().ThrowAsync<InvalidOperationException>();

            // Admin must still exist
            var users = await svc.ListUsersAsync();
            users.Should().ContainSingle(u => u.Id == admin.Id);
        }
        finally
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
