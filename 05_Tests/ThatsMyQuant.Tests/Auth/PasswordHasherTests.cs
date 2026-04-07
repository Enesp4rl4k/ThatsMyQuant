using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using ThatsMyQuant.Dashboard.Auth;

namespace ThatsMyQuant.Tests.Auth;

public class PasswordHasherTests
{
    // Feature: multi-user-auth, Property 2: hash format is always well-formed
    // Validates: Requirements 3.1, 3.2
    [Property(MaxTest = 10)]
    public Property P2_HashFormat_IsAlwaysWellFormed(NonEmptyString nonEmptyPassword)
    {
        var password = nonEmptyPassword.Get;
        var hash = PasswordHasher.Hash(password);

        var parts = hash.Split(':');

        // Must have exactly 3 segments (2 colons)
        if (parts.Length != 3)
            return false.ToProperty();

        // First segment must parse as int >= 100_000
        if (!int.TryParse(parts[0], out int iterations) || iterations < 100_000)
            return false.ToProperty();

        // Second segment must decode to exactly 16 bytes
        byte[] salt;
        try { salt = Convert.FromBase64String(parts[1]); }
        catch { return false.ToProperty(); }
        if (salt.Length != 16)
            return false.ToProperty();

        // Third segment must decode to exactly 64 bytes
        byte[] hashBytes;
        try { hashBytes = Convert.FromBase64String(parts[2]); }
        catch { return false.ToProperty(); }
        if (hashBytes.Length != 64)
            return false.ToProperty();

        // Overall format regex
        var formatOk = Regex.IsMatch(hash, @"^\d+:[A-Za-z0-9+/=]+:[A-Za-z0-9+/=]+$");
        return formatOk.ToProperty();
    }

    // Feature: multi-user-auth, Property 3: password hash round-trip
    // Validates: Requirements 3.3
    [Property(MaxTest = 10)]
    public Property P3_HashRoundTrip_VerifyAlwaysTrue(NonEmptyString nonEmptyPassword)
    {
        var password = nonEmptyPassword.Get;
        var hash = PasswordHasher.Hash(password);
        var result = PasswordHasher.Verify(password, hash);
        return result.ToProperty();
    }

    // Feature: multi-user-auth, Property 4: malformed hash never throws
    // Validates: Requirements 3.4
    [Property(MaxTest = 25)]
    public Property P4_MalformedHash_NeverThrows(string password, string malformedHash)
    {
        if (password is null) password = "";
        if (malformedHash is null) malformedHash = "";

        bool result;
        try
        {
            result = PasswordHasher.Verify(password, malformedHash);
        }
        catch
        {
            // Any exception is a violation of the property
            return false.ToProperty();
        }

        // For strings that are clearly not a well-formed hash (not 3 colon-separated parts),
        // Verify must return false.
        var parts = malformedHash.Split(':');
        if (parts.Length != 3)
            return (!result).ToProperty();

        // For anything else (could be a valid or invalid hash), we only assert no exception.
        return true.ToProperty();
    }
}
