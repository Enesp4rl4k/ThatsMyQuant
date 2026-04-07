using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface IUserAuthService
{
    Task                             SeedAsync();
    Task<OperatorUser?>              AuthenticateAsync(string email, string password);
    Task<OperatorUser>               CreateUserAsync(string username, string email, string password, Role role);
    Task                             UpdatePasswordAsync(Guid userId, string newPassword);
    Task<IReadOnlyList<OperatorUser>> ListUsersAsync();
    Task                             SetUserActiveAsync(Guid userId, bool isActive);
    Task                             DeleteUserAsync(Guid userId);
}
