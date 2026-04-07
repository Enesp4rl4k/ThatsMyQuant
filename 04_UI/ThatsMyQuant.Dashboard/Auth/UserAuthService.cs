using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Dashboard.Auth;

public class UserAuthService : IUserAuthService
{
    private const string DefaultAdminUsername = "admin";
    private const string DefaultAdminEmail = "admin@thatsmyquant.com";
    private const string DefaultAdminPassword = "Admin@123!";

    private readonly QuantDbContext _db;

    public UserAuthService(QuantDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Ensures the database exists and seeds the default admin if no users exist.
    /// </summary>
    public async Task SeedAsync()
    {
        var defaultAdmin = await _db.OperatorUsers
            .FirstOrDefaultAsync(u => u.Email.ToLower() == DefaultAdminEmail.ToLower());

        if (defaultAdmin is null)
        {
            defaultAdmin = new OperatorUser
            {
                Username           = DefaultAdminUsername,
                Email              = DefaultAdminEmail,
                PasswordHash       = PasswordHasher.Hash(DefaultAdminPassword),
                Role               = Role.Admin,
                IsActive           = true,
                MustChangePassword = false
            };
            _db.OperatorUsers.Add(defaultAdmin);
        }

        var mutated = false;

        if (!string.Equals(defaultAdmin.Username, DefaultAdminUsername, StringComparison.Ordinal))
        {
            defaultAdmin.Username = DefaultAdminUsername;
            mutated = true;
        }

        if (defaultAdmin.Role != Role.Admin)
        {
            defaultAdmin.Role = Role.Admin;
            mutated = true;
        }

        if (!defaultAdmin.IsActive)
        {
            defaultAdmin.IsActive = true;
            mutated = true;
        }

        if (defaultAdmin.MustChangePassword)
        {
            defaultAdmin.MustChangePassword = false;
            mutated = true;
        }

        if (!PasswordHasher.Verify(DefaultAdminPassword, defaultAdmin.PasswordHash))
        {
            defaultAdmin.PasswordHash = PasswordHasher.Hash(DefaultAdminPassword);
            mutated = true;
        }

        if (mutated || _db.Entry(defaultAdmin).State == EntityState.Added)
        {
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Authenticates a user by email (case-insensitive) and password.
    /// Returns null if not found, inactive, or wrong password.
    /// On success updates LastLoginAt and saves.
    /// </summary>
    public async Task<OperatorUser?> AuthenticateAsync(string email, string password)
    {
        var user = await _db.OperatorUsers
            .FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower());

        if (user is null)
            return null;

        if (!user.IsActive)
            return null;

        if (!PasswordHasher.Verify(password, user.PasswordHash))
            return null;

        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return user;
    }

    /// <summary>
    /// Creates a new user. Validates password complexity first.
    /// Throws ArgumentException if password is invalid.
    /// </summary>
    public async Task<OperatorUser> CreateUserAsync(string username, string email, string password, Role role)
    {
        var validationError = PasswordValidator.Validate(password);
        if (validationError is not null)
            throw new ArgumentException(validationError, nameof(password));

        var user = new OperatorUser
        {
            Username           = username,
            Email              = email,
            PasswordHash       = PasswordHasher.Hash(password),
            Role               = role,
            IsActive           = true,
            MustChangePassword = false
        };

        _db.OperatorUsers.Add(user);
        await _db.SaveChangesAsync();

        return user;
    }

    /// <summary>
    /// Updates a user's password. Validates complexity first.
    /// Throws ArgumentException if password is invalid.
    /// Sets MustChangePassword = false on success.
    /// </summary>
    public async Task UpdatePasswordAsync(Guid userId, string newPassword)
    {
        var validationError = PasswordValidator.Validate(newPassword);
        if (validationError is not null)
            throw new ArgumentException(validationError, nameof(newPassword));

        var user = await _db.OperatorUsers.FindAsync(userId)
            ?? throw new InvalidOperationException($"User {userId} not found.");

        user.PasswordHash       = PasswordHasher.Hash(newPassword);
        user.MustChangePassword = false;

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Returns all operator users.
    /// </summary>
    public async Task<IReadOnlyList<OperatorUser>> ListUsersAsync()
    {
        return await _db.OperatorUsers.ToListAsync();
    }

    /// <summary>
    /// Sets a user's active status.
    /// Guards against deactivating the last active Admin.
    /// </summary>
    public async Task SetUserActiveAsync(Guid userId, bool isActive)
    {
        var user = await _db.OperatorUsers.FindAsync(userId)
            ?? throw new InvalidOperationException($"User {userId} not found.");

        if (!isActive && user.Role == Role.Admin && user.IsActive)
        {
            var otherActiveAdmins = await _db.OperatorUsers
                .CountAsync(u => u.Id != userId && u.Role == Role.Admin && u.IsActive);

            if (otherActiveAdmins == 0)
                throw new InvalidOperationException(
                    "Cannot deactivate the last active Admin. Promote another user to Admin first.");
        }

        user.IsActive = isActive;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Deletes a user. Guards against deleting the last active Admin.
    /// </summary>
    public async Task DeleteUserAsync(Guid userId)
    {
        var user = await _db.OperatorUsers.FindAsync(userId)
            ?? throw new InvalidOperationException($"User {userId} not found.");

        if (user.Role == Role.Admin && user.IsActive)
        {
            var otherActiveAdmins = await _db.OperatorUsers
                .CountAsync(u => u.Id != userId && u.Role == Role.Admin && u.IsActive);

            if (otherActiveAdmins == 0)
                throw new InvalidOperationException(
                    "Cannot delete the last active Admin. Promote another user to Admin first.");
        }

        _db.OperatorUsers.Remove(user);
        await _db.SaveChangesAsync();
    }
}
