using System.Security.Cryptography;

namespace ThatsMyQuant.Dashboard.Auth;

public static class PasswordHasher
{
    private const int SaltBytes  = 16;
    private const int HashBytes  = 64;
    private const int Iterations = 100_000;

    /// <summary>
    /// Hashes a password using PBKDF2-SHA512.
    /// Returns a string in the format "{iterations}:{base64(salt)}:{base64(hash)}".
    /// </summary>
    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);

        using var pbkdf2 = new Rfc2898DeriveBytes(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA512);

        byte[] hash = pbkdf2.GetBytes(HashBytes);

        return $"{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Verifies a password against a stored hash.
    /// Returns false (never throws) on any malformed input or mismatch.
    /// </summary>
    public static bool Verify(string password, string storedHash)
    {
        try
        {
            string[] parts = storedHash.Split(':');
            if (parts.Length != 3)
                return false;

            if (!int.TryParse(parts[0], out int iterations))
                return false;

            byte[] salt         = Convert.FromBase64String(parts[1]);
            byte[] expectedHash = Convert.FromBase64String(parts[2]);

            using var pbkdf2 = new Rfc2898DeriveBytes(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA512);

            byte[] actualHash = pbkdf2.GetBytes(expectedHash.Length);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch
        {
            return false;
        }
    }
}
