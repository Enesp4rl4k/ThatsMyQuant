using System.Text.RegularExpressions;

namespace ThatsMyQuant.Dashboard.Auth;

public static class PasswordValidator
{
    private static readonly Regex _uppercase = new("[A-Z]", RegexOptions.Compiled);
    private static readonly Regex _digit     = new("[0-9]", RegexOptions.Compiled);
    private static readonly Regex _special   = new(@"[!@#$%^&*()\-_=+\[\]{};':"",./<>?/\\]", RegexOptions.Compiled);

    /// <summary>
    /// Returns null if the password satisfies all complexity rules;
    /// otherwise returns the first violated rule's error message.
    /// </summary>
    public static string? Validate(string password)
    {
        if (password.Length < 8)
            return "Password must be at least 8 characters.";

        if (!_uppercase.IsMatch(password))
            return "Password must contain at least one uppercase letter.";

        if (!_digit.IsMatch(password))
            return "Password must contain at least one digit.";

        if (!_special.IsMatch(password))
            return "Password must contain at least one special character.";

        return null;
    }
}
