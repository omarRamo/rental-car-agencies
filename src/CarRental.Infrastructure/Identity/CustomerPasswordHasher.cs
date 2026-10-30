using CarRental.Application.Customers.Contracts;
using Microsoft.AspNetCore.Identity;

namespace CarRental.Infrastructure.Identity;

/// <summary>
/// Hashes the passwords of the customer accounts with the hasher of ASP.NET Core Identity (PBKDF2-HMAC-SHA512, 100 000
/// iterations, random salt, versioned format), without a user store: the account is not an <see cref="ApplicationUser"/>
/// (ADR-0070). A rehash hint of the hasher is ignored on purpose: the format never changed since version 3.
/// </summary>
internal sealed class CustomerPasswordHasher : ICustomerPasswordHasher
{
    private readonly PasswordHasher<Subject> _hasher = new();

    private static readonly Subject Anyone = new();

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return _hasher.HashPassword(Anyone, password);
    }

    public bool Verify(string passwordHash, string password)
    {
        if (string.IsNullOrEmpty(passwordHash) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        try
        {
            return _hasher.VerifyHashedPassword(Anyone, passwordHash, password) is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
        }
        catch (FormatException)
        {
            // Not a hash of this hasher (a closed account stores a tombstone): never a match.
            return false;
        }
    }

    /// <summary>The hasher needs a user type; the customer accounts bring none.</summary>
    private sealed class Subject;
}
