using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface IUserAuthService
{
    Task                             SeedAsync();
    Task<OperatorUser?>              AuthenticateAsync(string email, string password);
    Task<OperatorUser>               CreateUserAsync(string username, string email, string password, Role role);
    /// <param name="requirePasswordChangeOnNextLogin">When true (e.g. admin reset), user must change password after next login.</param>
    Task                             UpdatePasswordAsync(Guid userId, string newPassword, bool requirePasswordChangeOnNextLogin = false);
    Task<IReadOnlyList<OperatorUser>> ListUsersAsync();
    Task                             SetUserActiveAsync(Guid userId, bool isActive);
    Task                             DeleteUserAsync(Guid userId);
}
