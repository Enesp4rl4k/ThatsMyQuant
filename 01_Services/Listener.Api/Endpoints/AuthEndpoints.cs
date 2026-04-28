using System.Security.Claims;
using Listener.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Infrastructure.Persistence;

namespace Listener.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth").WithTags("Auth");
        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapGet("/me", Me).RequireAuthorization();
        group.MapGet("/config", GetConfig).AllowAnonymous();

        // User CRUD — admin-only when auth is on, anonymous when auth disabled
        group.MapGet("/users", ListUsers);
        group.MapPost("/users", CreateUser);
        group.MapPatch("/users/{id:guid}", UpdateUser);
        group.MapPost("/users/{id:guid}/password", ResetPassword);
        group.MapDelete("/users/{id:guid}", DeleteUser);
        return endpoints;
    }

    private static async Task<IResult> ListUsers(QuantDbContext db, CancellationToken ct)
    {
        var users = await db.OperatorUsers
            .AsNoTracking()
            .OrderBy(u => u.CreatedAt)
            .Select(u => new UserListDto(
                u.Id, u.Username, u.Email, u.Role.ToString(),
                u.IsActive, u.MustChangePassword, u.CreatedAt, u.LastLoginAt))
            .ToArrayAsync(ct);
        return Results.Ok(users);
    }

    private static async Task<IResult> CreateUser(
        [FromBody] CreateUserRequest req,
        QuantDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return Results.BadRequest(new { error = "username, email, password required" });

        var emailLower = req.Email.Trim().ToLower();
        if (await db.OperatorUsers.AnyAsync(u => u.Email.ToLower() == emailLower, ct))
            return Results.Conflict(new { error = "email already exists" });

        if (!Enum.TryParse<ThatsMyQuant.Contracts.Models.Role>(req.Role, true, out var role))
            role = ThatsMyQuant.Contracts.Models.Role.ReadOnly;

        var user = new ThatsMyQuant.Contracts.Models.OperatorUser
        {
            Username = req.Username.Trim(),
            Email = emailLower,
            PasswordHash = PasswordHasher.Hash(req.Password),
            Role = role,
            IsActive = true,
            MustChangePassword = false,
        };
        db.OperatorUsers.Add(user);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new UserListDto(
            user.Id, user.Username, user.Email, user.Role.ToString(),
            user.IsActive, user.MustChangePassword, user.CreatedAt, user.LastLoginAt));
    }

    private static async Task<IResult> UpdateUser(
        Guid id,
        [FromBody] UpdateUserRequest req,
        QuantDbContext db,
        CancellationToken ct)
    {
        var user = await db.OperatorUsers.FindAsync(new object?[] { id }, ct);
        if (user == null) return Results.NotFound();

        if (!string.IsNullOrWhiteSpace(req.Username)) user.Username = req.Username.Trim();
        if (!string.IsNullOrWhiteSpace(req.Email)) user.Email = req.Email.Trim().ToLower();
        if (!string.IsNullOrWhiteSpace(req.Role) &&
            Enum.TryParse<ThatsMyQuant.Contracts.Models.Role>(req.Role, true, out var role))
        {
            if (user.Role == ThatsMyQuant.Contracts.Models.Role.Admin && role != ThatsMyQuant.Contracts.Models.Role.Admin)
            {
                var others = await db.OperatorUsers.CountAsync(
                    u => u.Id != id && u.Role == ThatsMyQuant.Contracts.Models.Role.Admin && u.IsActive, ct);
                if (others == 0)
                    return Results.BadRequest(new { error = "Cannot demote the last active admin." });
            }
            user.Role = role;
        }
        if (req.IsActive.HasValue)
        {
            if (user.Role == ThatsMyQuant.Contracts.Models.Role.Admin && user.IsActive && !req.IsActive.Value)
            {
                var others = await db.OperatorUsers.CountAsync(
                    u => u.Id != id && u.Role == ThatsMyQuant.Contracts.Models.Role.Admin && u.IsActive, ct);
                if (others == 0)
                    return Results.BadRequest(new { error = "Cannot deactivate the last active admin." });
            }
            user.IsActive = req.IsActive.Value;
        }
        await db.SaveChangesAsync(ct);

        return Results.Ok(new UserListDto(
            user.Id, user.Username, user.Email, user.Role.ToString(),
            user.IsActive, user.MustChangePassword, user.CreatedAt, user.LastLoginAt));
    }

    private static async Task<IResult> ResetPassword(
        Guid id,
        [FromBody] ResetPasswordRequest req,
        QuantDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 6)
            return Results.BadRequest(new { error = "password must be at least 6 chars" });

        var user = await db.OperatorUsers.FindAsync(new object?[] { id }, ct);
        if (user == null) return Results.NotFound();

        user.PasswordHash = PasswordHasher.Hash(req.NewPassword);
        user.MustChangePassword = req.RequireChangeOnLogin;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { ok = true });
    }

    private static async Task<IResult> DeleteUser(Guid id, QuantDbContext db, CancellationToken ct)
    {
        var user = await db.OperatorUsers.FindAsync(new object?[] { id }, ct);
        if (user == null) return Results.NotFound();

        if (user.Role == ThatsMyQuant.Contracts.Models.Role.Admin && user.IsActive)
        {
            var others = await db.OperatorUsers.CountAsync(
                u => u.Id != id && u.Role == ThatsMyQuant.Contracts.Models.Role.Admin && u.IsActive, ct);
            if (others == 0)
                return Results.BadRequest(new { error = "Cannot delete the last active admin." });
        }

        db.OperatorUsers.Remove(user);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static IResult GetConfig(IOptions<AuthOptions> options) =>
        Results.Ok(new { enabled = options.Value.Enabled });

    private static async Task<IResult> LoginAsync(
        [FromBody] LoginRequest req,
        QuantDbContext db,
        JwtTokenService tokens,
        IOptions<AuthOptions> options,
        CancellationToken ct)
    {
        if (!options.Value.Enabled)
            return Results.BadRequest(new { error = "Auth is disabled on this server." });

        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return Results.BadRequest(new { error = "Email and password are required." });

        var emailLower = req.Email.Trim().ToLower();
        var user = await db.OperatorUsers.FirstOrDefaultAsync(u => u.Email.ToLower() == emailLower, ct);

        if (user is null || !user.IsActive || !PasswordHasher.Verify(req.Password, user.PasswordHash))
        {
            // Constant-ish 401 to avoid email enumeration
            await Task.Delay(150, ct);
            return Results.Json(new { error = "Invalid email or password." }, statusCode: 401);
        }

        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var token = tokens.IssueToken(user, out var expiresUtc);
        return Results.Ok(new LoginResponse(
            Token: token,
            ExpiresUtc: expiresUtc,
            User: new UserInfoDto(
                user.Id,
                user.Username,
                user.Email,
                user.Role.ToString(),
                user.MustChangePassword)));
    }

    private static async Task<IResult> Me(
        ClaimsPrincipal principal,
        QuantDbContext db,
        CancellationToken ct)
    {
        var sub = principal.FindFirstValue("sub")
                  ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(sub, out var id))
            return Results.Unauthorized();

        var user = await db.OperatorUsers.FindAsync(new object?[] { id }, ct);
        if (user is null || !user.IsActive) return Results.Unauthorized();

        return Results.Ok(new UserInfoDto(
            user.Id,
            user.Username,
            user.Email,
            user.Role.ToString(),
            user.MustChangePassword));
    }
}

public sealed record LoginRequest(string Email, string Password);

public sealed record LoginResponse(string Token, DateTime ExpiresUtc, UserInfoDto User);

public sealed record UserInfoDto(
    Guid Id,
    string Username,
    string Email,
    string Role,
    bool MustChangePassword);

public sealed record UserListDto(
    Guid Id,
    string Username,
    string Email,
    string Role,
    bool IsActive,
    bool MustChangePassword,
    DateTime CreatedAt,
    DateTime? LastLoginAt);

public sealed record CreateUserRequest(string Username, string Email, string Password, string Role);

public sealed record UpdateUserRequest(string? Username, string? Email, string? Role, bool? IsActive);

public sealed record ResetPasswordRequest(string NewPassword, bool RequireChangeOnLogin);
