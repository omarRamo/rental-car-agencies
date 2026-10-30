using CarRental.Domain.Common;

namespace CarRental.Domain.Customers.Accounts;

/// <summary>Why a code was e-mailed to the customer.</summary>
public enum CustomerCodePurpose
{
    /// <summary>Proves that the customer reads the address of the account.</summary>
    EmailVerification = 1,

    /// <summary>Lets the customer choose a new password.</summary>
    PasswordReset = 2,
}

/// <summary>
/// A six-digit code e-mailed to a customer (ADR-0070): salted hash stored, valid a short time, single use, a few
/// attempts (<see cref="MaxAttempts"/>) before it is invalidated. Issuing a new code invalidates the previous ones of
/// the same purpose (the service does it). Belongs to the agency of its account.
/// </summary>
public sealed class CustomerAccountCode : Entity, IAgencyOwned
{
    public const int MaxAttempts = 5;

    private CustomerAccountCode()
    {
    }

    private CustomerAccountCode(Guid agencyId, Guid accountId, CustomerCodePurpose purpose, byte[] codeHash, byte[] codeSalt, DateTimeOffset now, TimeSpan validity)
        : base(NewId())
    {
        AgencyId = agencyId;
        AccountId = accountId;
        Purpose = purpose;
        CodeHash = codeHash;
        CodeSalt = codeSalt;
        CreatedAtUtc = now.ToUniversalTime();
        ExpiresAtUtc = CreatedAtUtc + validity;
    }

    public Guid AgencyId { get; private set; }

    public Guid AccountId { get; private set; }

    public CustomerCodePurpose Purpose { get; private set; }

    public byte[] CodeHash { get; private set; } = null!;

    public byte[] CodeSalt { get; private set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? UsedAtUtc { get; private set; }

    public int FailedAttempts { get; private set; }

    /// <summary>Too many wrong codes, or a newer code was issued.</summary>
    public DateTimeOffset? InvalidatedAtUtc { get; private set; }

    /// <summary>Issues a code for <paramref name="purpose"/>; returns the entity and the code in clear (e-mailed, never stored).</summary>
    public static (CustomerAccountCode Entity, string Code) Issue(Guid agencyId, Guid accountId, CustomerCodePurpose purpose, DateTimeOffset now, TimeSpan validity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(validity, TimeSpan.Zero);
        if (agencyId == Guid.Empty || accountId == Guid.Empty)
        {
            throw new ArgumentException("A code belongs to an account of an agency.");
        }

        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown code purpose.");
        }

        var code = AccountSecrets.NewCode();
        var salt = AccountSecrets.NewSalt();
        return (new CustomerAccountCode(agencyId, accountId, purpose, AccountSecrets.HashCode(code, salt), salt, now, validity), code);
    }

    public bool IsUsableAt(DateTimeOffset now) => UsedAtUtc is null && InvalidatedAtUtc is null && ExpiresAtUtc > now;

    /// <summary>
    /// Checks a typed code: success marks it used; a wrong code counts an attempt and the last one invalidates the code.
    /// Every failure is <see cref="CustomerAccountErrors.CodeInvalid"/>.
    /// </summary>
    public Result TryUse(string? typed, DateTimeOffset now)
    {
        if (!IsUsableAt(now))
        {
            return CustomerAccountErrors.CodeInvalid;
        }

        var code = AccountSecrets.NormalizeCode(typed);
        if (code is not null && AccountSecrets.SameHash(CodeHash, AccountSecrets.HashCode(code, CodeSalt)))
        {
            UsedAtUtc = now.ToUniversalTime();
            return Result.Success();
        }

        FailedAttempts++;
        if (FailedAttempts >= MaxAttempts)
        {
            InvalidatedAtUtc = now.ToUniversalTime();
        }

        return CustomerAccountErrors.CodeInvalid;
    }

    /// <summary>A newer code replaces this one.</summary>
    public void Invalidate(DateTimeOffset now)
    {
        if (UsedAtUtc is null)
        {
            InvalidatedAtUtc ??= now.ToUniversalTime();
        }
    }
}
