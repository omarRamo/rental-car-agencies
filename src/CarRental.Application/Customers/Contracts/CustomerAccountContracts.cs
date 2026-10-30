using CarRental.Application.Common.Email;

namespace CarRental.Application.Customers.Contracts;

/// <summary>Hashes the passwords of the customer accounts (implemented by Infrastructure with the PBKDF2 hasher of ASP.NET Core Identity).</summary>
public interface ICustomerPasswordHasher
{
    string Hash(string password);

    /// <summary>Whether <paramref name="password"/> matches <paramref name="passwordHash"/> (constant time, never throws on a malformed hash).</summary>
    bool Verify(string passwordHash, string password);
}

/// <summary>A customer account as the application shows it (never the password hash); the security stamp changes with the credentials and is carried by the access tokens.</summary>
public sealed record CustomerAccountInfo(
    Guid Id,
    Guid AgencyId,
    string Email,
    bool EmailVerified,
    string FirstName,
    string LastName,
    string? Phone,
    string Culture,
    Guid? PersonId,
    string SecurityStamp,
    DateTimeOffset CreatedAtUtc);

/// <summary>An open session of the application: the account and the refresh token (shown once, never stored in clear).</summary>
public sealed record CustomerSession(CustomerAccountInfo Account, string RefreshToken, DateTimeOffset RefreshTokenExpiresAtUtc);

/// <summary>What the customer types to open an account; the device is how the application names the phone.</summary>
public sealed record CustomerRegistrationRequest(
    string? Email,
    string? Password,
    string? FirstName,
    string? LastName,
    string? Phone = null,
    string? Culture = null,
    string? Device = null);

public sealed record CustomerSignInRequest(string? Email, string? Password, string? Device = null);

public sealed record CustomerProfileUpdateRequest(string? FirstName, string? LastName, string? Phone = null, string? Culture = null);

/// <summary>Why a code is e-mailed to a customer.</summary>
public enum CustomerAccountEmailKind
{
    EmailVerification = 1,
    PasswordReset = 2,
}

/// <summary>An e-mail of a customer account: a six-digit code, in the language of the account, from the agency of the account.</summary>
public sealed record CustomerAccountEmail(
    CustomerAccountEmailKind Kind,
    Guid AgencyId,
    string Email,
    string Name,
    string Culture,
    string Code,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Outbox message of a code to e-mail (ADR-0070): composed by the web host with its templates and queued as an e-mail
/// once the transaction that issued the code is committed. Holds the code in clear, as the e-mail it becomes does
/// (outbox payloads are never shown).
/// </summary>
public sealed record CustomerAccountEmailRequested(CustomerAccountEmail Email);

/// <summary>Composes the e-mails of the customer accounts (web host: templates and resources).</summary>
public interface ICustomerAccountEmailComposer
{
    Task<EmailMessage> ComposeAsync(CustomerAccountEmail email, CancellationToken cancellationToken = default);
}
