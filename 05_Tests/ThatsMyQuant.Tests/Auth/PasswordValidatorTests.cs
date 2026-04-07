// Feature: multi-user-auth, Property 12: PasswordValidator rejects non-compliant passwords
// Validates: Requirements 10.1, 10.2, 10.3, 10.4, 10.5

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using ThatsMyQuant.Dashboard.Auth;

namespace ThatsMyQuant.Tests.Auth;

public class PasswordValidatorTests
{
    // ── character sets ────────────────────────────────────────────────────────

    private static readonly char[] Uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();
    private static readonly char[] Lowercase = "abcdefghijklmnopqrstuvwxyz".ToCharArray();
    private static readonly char[] Digits    = "0123456789".ToCharArray();
    private static readonly char[] Specials  = "!@#$%^&*()_+-=[]{};\':\",./<>?".ToCharArray();

    // ── helpers ───────────────────────────────────────────────────────────────

    private static Gen<char> PickChar(char[] pool) => Gen.Elements<char>(pool);

    private static Gen<string> StringOfLen(int len, char[] pool) =>
        Gen.ArrayOf<char>(PickChar(pool), len).Select(cs => new string(cs));

    // ── P12a: too-short passwords are rejected ────────────────────────────────
    // Validates: Requirement 10.1
    [Property(MaxTest = 25)]
    public Property P12a_TooShort_IsRejected()
    {
        // Strings of length 0–7 (all chars allowed so other rules may pass, but length fails first)
        var allChars = Uppercase.Concat(Lowercase).Concat(Digits).Concat(Specials).ToArray();
        var gen =
            from len in Gen.Choose(0, 7)
            from s in StringOfLen(len, allChars)
            select s;

        return Prop.ForAll(gen.ToArbitrary(), password =>
        {
            var result = PasswordValidator.Validate(password);
            return result != null && result.Length > 0;
        });
    }

    // ── P12b: passwords with no uppercase are rejected ────────────────────────
    // Validates: Requirement 10.2
    [Property(MaxTest = 25)]
    public Property P12b_NoUppercase_IsRejected()
    {
        // ≥ 8 chars, only lowercase + digits + specials (no uppercase)
        var noUpperChars = Lowercase.Concat(Digits).Concat(Specials).ToArray();
        var gen =
            from len in Gen.Choose(8, 20)
            from s in StringOfLen(len, noUpperChars)
            select s;

        return Prop.ForAll(gen.ToArbitrary(), password =>
        {
            var result = PasswordValidator.Validate(password);
            return result != null && result.Length > 0;
        });
    }

    // ── P12c: passwords with no digit are rejected ────────────────────────────
    // Validates: Requirement 10.3
    [Property(MaxTest = 25)]
    public Property P12c_NoDigit_IsRejected()
    {
        // ≥ 8 chars, has uppercase, no digits
        var noDigitChars = Uppercase.Concat(Lowercase).Concat(Specials).ToArray();
        var gen =
            from len in Gen.Choose(7, 19)
            from s in StringOfLen(len, noDigitChars)
            // Prepend 'A' to guarantee at least one uppercase; total length ≥ 8
            select "A" + s;

        return Prop.ForAll(gen.ToArbitrary(), password =>
        {
            var result = PasswordValidator.Validate(password);
            return result != null && result.Length > 0;
        });
    }

    // ── P12d: passwords with no special character are rejected ────────────────
    // Validates: Requirement 10.4
    [Property(MaxTest = 25)]
    public Property P12d_NoSpecial_IsRejected()
    {
        // ≥ 8 chars, has uppercase + digit, no specials
        var noSpecialChars = Uppercase.Concat(Lowercase).Concat(Digits).ToArray();
        var gen =
            from len in Gen.Choose(6, 18)
            from s in StringOfLen(len, noSpecialChars)
            // Prepend "A1" to guarantee uppercase + digit; total length ≥ 8
            select "A1" + s;

        return Prop.ForAll(gen.ToArbitrary(), password =>
        {
            var result = PasswordValidator.Validate(password);
            return result != null && result.Length > 0;
        });
    }

    // ── P12e: compliant passwords are accepted ────────────────────────────────
    // Validates: Requirements 10.1, 10.2, 10.3, 10.4, 10.5
    [Property(MaxTest = 25)]
    public Property P12e_CompliantPassword_IsAccepted()
    {
        // Build: lowerPad (0–12 chars) + one uppercase + one digit + one special
        // Total ≥ 3; pad to 8 if needed
        var gen =
            from lowerLen in Gen.Choose(0, 12)
            from lowerPad in StringOfLen(lowerLen, Lowercase)
            from upper   in PickChar(Uppercase)
            from digit   in PickChar(Digits)
            from special in PickChar(Specials)
            let core = lowerPad + upper + digit + special
            let padded = core.Length >= 8 ? core : core + new string('a', 8 - core.Length)
            select padded;

        return Prop.ForAll(gen.ToArbitrary(), password =>
        {
            var result = PasswordValidator.Validate(password);
            return result == null;
        });
    }
}
