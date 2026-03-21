using Xunit;
using System.Security.Cryptography;
using System.Text;

namespace ThatsMyQuant.SecurityTests;

public class AuthServiceTests
{
    // Simulating a simple PBKDF2 password hasher that might be used in the actual service
    private string HashPassword(string password, string salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, Encoding.UTF8.GetBytes(salt), 100000, HashAlgorithmName.SHA256);
        byte[] hash = pbkdf2.GetBytes(32);
        return Convert.ToBase64String(hash);
    }

    [Fact]
    public void VerifyPassword_ShouldSucceed_WithCorrectCredentials()
    {
        // Arrange
        var password = "SuperSecretAdminPassword123!";
        var salt = "random_account_salt_99";
        var dbHash = HashPassword(password, salt);

        // Act
        var loginAttemptHash = HashPassword("SuperSecretAdminPassword123!", salt);

        // Assert
        Assert.Equal(dbHash, loginAttemptHash);
    }

    [Fact]
    public void VerifyPassword_ShouldFail_WithIncorrectCredentials()
    {
        // Arrange
        var password = "SuperSecretAdminPassword123!";
        var salt = "random_account_salt_99";
        var dbHash = HashPassword(password, salt);

        // Act
        var loginAttemptHash = HashPassword("WrongPassword", salt);

        // Assert
        Assert.NotEqual(dbHash, loginAttemptHash);
    }
}
