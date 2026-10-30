using CarRental.Domain.Common;

namespace CarRental.Domain.Customers.Accounts;

/// <summary>
/// The account a customer opens in the mobile application or on the storefront of an agency (ADR-0070): e-mail and
/// password, a name and a phone to pre-fill the bookings, the language of the e-mails. An account belongs to one agency
/// (the same person opens one account per agency that rents to them, like a <see cref="Person"/>): its e-mail is unique
/// within the agency. Once a booking is made, the account is linked to the <see cref="Person"/> registered as its main
/// driver (<see cref="PersonId"/>), so that the bookings of that person show in the application.
/// </summary>
/// <remarks>
/// The password is stored as a hash computed by the adapter; the <see cref="SecurityStamp"/> changes with the
/// credentials (every refresh token is revoked by the service at the same time). Sign-in failures lock the account for a
/// while (counted per account; the per-address rate limit protects every account at once). Closing an account scrubs it
/// and frees its e-mail address: the bookings and the person stay with the agency under its retention rules.
/// </remarks>
public sealed class CustomerAccount : AggregateRoot, IAgencyOwned
{
    public const int NameMaxLength = Person.NameMaxLength;
    public const int PasswordHashMaxLength = 512;
    public const int SecurityStampLength = 32;
    public const int CultureMaxLength = 5;

    /// <summary>Languages of the e-mails and of the pages the application opens.</summary>
    public static readonly IReadOnlyList<string> Cultures = ["fr", "en"];

    private CustomerAccount()
    {
    }

    private CustomerAccount(Guid id, Guid agencyId, string email, string passwordHash, string firstName, string lastName, string? phone, string culture, DateTimeOffset now)
        : base(id)
    {
        AgencyId = agencyId;
        Email = email;
        PasswordHash = passwordHash;
        FirstName = firstName;
        LastName = lastName;
        Phone = phone;
        Culture = culture;
        SecurityStamp = AccountSecrets.NewStamp();
        CreatedAtUtc = now.ToUniversalTime();
        LastModifiedAtUtc = CreatedAtUtc;
    }

    public Guid AgencyId { get; private set; }

    /// <summary>Lower case, unique within the agency.</summary>
    public string Email { get; private set; } = null!;

    public DateTimeOffset? EmailVerifiedAtUtc { get; private set; }

    /// <summary>The hash of the password (never the password), as the adapter wrote it.</summary>
    public string PasswordHash { get; private set; } = null!;

    public string FirstName { get; private set; } = null!;

    public string LastName { get; private set; } = null!;

    /// <summary>E.164, e.g. <c>+21698123456</c>.</summary>
    public string? Phone { get; private set; }

    /// <summary><c>fr</c> or <c>en</c>.</summary>
    public string Culture { get; private set; } = null!;

    /// <summary>The person registered as the main driver of the first booking made with the account.</summary>
    public Guid? PersonId { get; private set; }

    /// <summary>Changes with the credentials; carried by the access tokens so that a stolen token dies with a password change.</summary>
    public string SecurityStamp { get; private set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset LastModifiedAtUtc { get; private set; }

    public DateTimeOffset? LastSignInAtUtc { get; private set; }

    public int FailedSignInCount { get; private set; }

    public DateTimeOffset? LockedUntilUtc { get; private set; }

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    public bool IsEmailVerified => EmailVerifiedAtUtc is not null;

    public bool IsClosed => ClosedAtUtc is not null;

    public string FullName => $"{FirstName} {LastName}";

    /// <summary>
    /// Opens an account for <paramref name="agencyId"/> with the details typed in the application and the hash of a
    /// password already checked by <see cref="CustomerPasswords"/>; the caller checks that the e-mail is free.
    /// </summary>
    public static Result<CustomerAccount> Register(Guid agencyId, CustomerAccountDetails details, string passwordHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        if (agencyId == Guid.Empty)
        {
            return CustomerAccountErrors.AgencyRequired;
        }

        var email = EmailAddresses.Normalize(details.Email);
        if (email.IsFailure)
        {
            return email.Error;
        }

        var profile = ProfileFacts.From(details.FirstName, details.LastName, details.Phone, details.Culture);
        if (profile.IsFailure)
        {
            return profile.Error;
        }

        var account = new CustomerAccount(NewId(), agencyId, email.Value, passwordHash, profile.Value.FirstName, profile.Value.LastName, profile.Value.Phone, profile.Value.Culture, now);
        account.Raise(new CustomerAccountRegistered(account.Id, agencyId));
        return account;
    }

    public Result UpdateProfile(CustomerProfileDetails details, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (IsClosed)
        {
            return CustomerAccountErrors.Closed;
        }

        var profile = ProfileFacts.From(details.FirstName, details.LastName, details.Phone, details.Culture);
        if (profile.IsFailure)
        {
            return profile.Error;
        }

        FirstName = profile.Value.FirstName;
        LastName = profile.Value.LastName;
        Phone = profile.Value.Phone;
        Culture = profile.Value.Culture;
        LastModifiedAtUtc = now.ToUniversalTime();
        Raise(new CustomerAccountProfileUpdated(Id));
        return Result.Success();
    }

    /// <summary>True while the account is locked after too many failures.</summary>
    public bool IsLockedAt(DateTimeOffset now) => LockedUntilUtc is { } until && until > now;

    /// <summary>A successful sign-in: the failures are forgotten.</summary>
    public void RecordSignIn(DateTimeOffset now)
    {
        LastSignInAtUtc = now.ToUniversalTime();
        FailedSignInCount = 0;
        LockedUntilUtc = null;
    }

    /// <summary>A wrong password; the <paramref name="maxFailures"/>th in a row locks the account for <paramref name="lockout"/>. Returns true when it did.</summary>
    public bool RecordFailedSignIn(DateTimeOffset now, int maxFailures, TimeSpan lockout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFailures, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lockout, TimeSpan.Zero);
        FailedSignInCount++;
        if (FailedSignInCount < maxFailures)
        {
            return false;
        }

        FailedSignInCount = 0;
        LockedUntilUtc = now.ToUniversalTime() + lockout;
        return true;
    }

    public Result VerifyEmail(DateTimeOffset now)
    {
        if (IsClosed)
        {
            return CustomerAccountErrors.Closed;
        }

        if (IsEmailVerified)
        {
            return CustomerAccountErrors.AlreadyVerified;
        }

        EmailVerifiedAtUtc = now.ToUniversalTime();
        LastModifiedAtUtc = EmailVerifiedAtUtc.Value;
        Raise(new CustomerAccountEmailVerified(Id));
        return Result.Success();
    }

    /// <summary>A new password (hash already computed): the stamp changes, the lock and the failures are cleared.</summary>
    public Result ChangePassword(string passwordHash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        if (IsClosed)
        {
            return CustomerAccountErrors.Closed;
        }

        PasswordHash = passwordHash;
        SecurityStamp = AccountSecrets.NewStamp();
        FailedSignInCount = 0;
        LockedUntilUtc = null;
        LastModifiedAtUtc = now.ToUniversalTime();
        Raise(new CustomerAccountPasswordChanged(Id));
        return Result.Success();
    }

    /// <summary>Remembers the person the account books for (the main driver of its first booking); never changes afterwards.</summary>
    public void LinkPerson(Guid personId)
    {
        if (personId == Guid.Empty)
        {
            throw new ArgumentException("A person id cannot be empty.", nameof(personId));
        }

        PersonId ??= personId;
    }

    /// <summary>
    /// Closes the account at the request of the customer: the names and the phone are scrubbed, the e-mail replaced by a
    /// tombstone (the address can open a new account), the credentials invalidated. Bookings and the person are kept.
    /// </summary>
    public void Close(DateTimeOffset now)
    {
        if (IsClosed)
        {
            return;
        }

        ClosedAtUtc = now.ToUniversalTime();
        LastModifiedAtUtc = ClosedAtUtc.Value;
        Email = $"closed-{Id:N}@account.invalid";
        FirstName = "-";
        LastName = "-";
        Phone = null;
        SecurityStamp = AccountSecrets.NewStamp();
        PasswordHash = "closed";
        Raise(new CustomerAccountClosed(Id));
    }

    private sealed record ProfileFacts(string FirstName, string LastName, string? Phone, string Culture)
    {
        public static Result<ProfileFacts> From(string? firstName, string? lastName, string? phone, string? culture)
        {
            var first = TextRules.Clean(firstName);
            var last = TextRules.Clean(lastName);
            if (!TextRules.IsValidName(first, NameMaxLength) || !TextRules.IsValidName(last, NameMaxLength))
            {
                return CustomerAccountErrors.NameInvalid;
            }

            string? normalizedPhone = null;
            if (!string.IsNullOrWhiteSpace(phone))
            {
                var normalized = PhoneNumbers.Normalize(phone);
                if (normalized.IsFailure)
                {
                    return normalized.Error;
                }

                normalizedPhone = normalized.Value;
            }

            var language = string.IsNullOrWhiteSpace(culture) ? Cultures[0] : culture.Trim().ToLowerInvariant();
            if (!Cultures.Contains(language, StringComparer.Ordinal))
            {
                return CustomerAccountErrors.CultureInvalid;
            }

            return new ProfileFacts(first!, last!, normalizedPhone, language);
        }
    }
}

/// <summary>What the customer types to open an account.</summary>
public sealed record CustomerAccountDetails(string? Email, string? FirstName, string? LastName, string? Phone = null, string? Culture = null);

/// <summary>The profile of an account as the customer edits it.</summary>
public sealed record CustomerProfileDetails(string? FirstName, string? LastName, string? Phone = null, string? Culture = null);
