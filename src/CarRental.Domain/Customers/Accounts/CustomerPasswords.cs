using CarRental.Domain.Common;

namespace CarRental.Domain.Customers.Accounts;

/// <summary>
/// The password rules of the customer accounts (ADR-0070, ASVS 2.1): length only (at least <see cref="MinLength"/>
/// characters, at most <see cref="MaxLength"/>, every character counting, spaces included), no composition rule, and a
/// refusal of the most common passwords and of the e-mail address itself. Hashing is the job of the adapter (PBKDF2 of
/// ASP.NET Core Identity).
/// </summary>
public static class CustomerPasswords
{
    public const int MinLength = 10;

    public const int MaxLength = 128;

    /// <summary>Passwords that pass the length rule and that every dictionary attack tries first (lower case).</summary>
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "1234567890", "0123456789", "12345678910", "123456789012", "password12", "password123", "passwordpassword",
        "motdepasse", "motdepasse1", "motdepasse123", "qwertyuiop", "azertyuiop", "iloveyou12", "football12", "1q2w3e4r5t",
        "abcdefghij", "aaaaaaaaaa", "1111111111", "0000000000", "9876543210", "administrator", "welcome123", "bonjour123",
    };

    /// <summary>Success, or <see cref="CustomerAccountErrors.PasswordWeak"/>.</summary>
    public static Result Validate(string? password, string? email = null)
    {
        if (password is null || password.Length is < MinLength or > MaxLength || string.IsNullOrWhiteSpace(password))
        {
            return CustomerAccountErrors.PasswordWeak;
        }

        var lower = password.ToLowerInvariant();
        if (Common.Contains(lower) || lower.Distinct().Count() < 3)
        {
            return CustomerAccountErrors.PasswordWeak;
        }

        if (email is not null && string.Equals(lower, email.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            return CustomerAccountErrors.PasswordWeak;
        }

        return Result.Success();
    }
}
