using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Customers.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Customers.Accounts;

/// <summary>
/// The customer accounts of an agency (ADR-0070): registration, sign-in with lockout, rotating refresh tokens with
/// replay detection, e-mail verification and password reset by six-digit codes, profile, closing. Runs for the agency of
/// the operation (<see cref="IUserContext.AgencyId"/>: the agency of the site or of the application the request came
/// from); an account of another agency is unknown. The caller (the API) checks who the request comes from: a method that
/// takes an account id trusts it.
/// </summary>
/// <remarks>
/// <para>Answers never tell whether an e-mail address has an account: a sign-in with an unknown address costs a password
/// check too, a password reset is "sent" whatever the address. Every code and token is stored hashed.</para>
/// <para>A refusal that must leave a trace (a failed sign-in counted towards the lockout, a wrong code counted towards
/// the invalidation of the code, a replayed token revoking its family) is committed: the unit of work returns it as a
/// value (<see cref="Refusal{T}"/>), never as a failed result, which would roll the trace back.</para>
/// </remarks>
public sealed partial class CustomerAccountService(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IUserContext user,
    ICustomerPasswordHasher hasher,
    ISettingsReader settings,
    IOutbox outbox,
    TimeProvider clock,
    ILogger<CustomerAccountService> logger)
{
    private static string? _dummyHash;

    /// <summary>Opens an account, signs it in (refresh token) and e-mails a verification code.</summary>
    public async Task<Result<CustomerSession>> RegisterAsync(CustomerRegistrationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (user.AgencyId is not { } agencyId)
        {
            return CustomerAccountErrors.AgencyRequired;
        }

        var password = CustomerPasswords.Validate(request.Password, request.Email);
        if (password.IsFailure)
        {
            return password.Error;
        }

        var now = clock.GetUtcNow();
        var account = CustomerAccount.Register(
            agencyId,
            new CustomerAccountDetails(request.Email, request.FirstName, request.LastName, request.Phone, request.Culture),
            hasher.Hash(request.Password!),
            now);
        if (account.IsFailure)
        {
            return account.Error;
        }

        var lifetime = await settings.GetAsync(CustomerAccountSettings.RefreshTokenLifetime, agencyId, cancellationToken);
        var validity = await settings.GetAsync(CustomerAccountSettings.CodeValidity, agencyId, cancellationToken);
        try
        {
            return await unitOfWork.ExecuteGuardedAsync<CustomerSession>(
                async ct =>
                {
                    if (await db.Set<CustomerAccount>().AnyAsync(a => a.AgencyId == agencyId && a.Email == account.Value.Email, ct))
                    {
                        return CustomerAccountErrors.EmailTaken;
                    }

                    db.Set<CustomerAccount>().Add(account.Value);
                    IssueCode(account.Value, CustomerCodePurpose.EmailVerification, CustomerAccountEmailKind.EmailVerification, now, validity);
                    var (token, clear) = NewRefreshToken(account.Value, Guid.CreateVersion7(), request.Device, now, lifetime);
                    db.Set<CustomerRefreshToken>().Add(token);
                    LogRegistered(account.Value.Id, agencyId);
                    return new CustomerSession(Map(account.Value), clear, token.ExpiresAtUtc);
                },
                cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two registrations of the same address at the same instant: the unique index of the agency decides.
            return CustomerAccountErrors.EmailTaken;
        }
    }

    /// <summary>E-mail and password: a new refresh token family (one per device). A wrong password is counted (committed) towards the lockout.</summary>
    public async Task<Result<CustomerSession>> SignInAsync(CustomerSignInRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (user.AgencyId is not { } agencyId)
        {
            return CustomerAccountErrors.AgencyRequired;
        }

        var email = request.Email?.Trim().ToLowerInvariant();
        var password = request.Password ?? string.Empty;
        if (string.IsNullOrEmpty(email) || password.Length == 0 || password.Length > CustomerPasswords.MaxLength)
        {
            return CustomerAccountErrors.CredentialsInvalid;
        }

        var maxFailures = await settings.GetAsync(CustomerAccountSettings.LockoutFailures, agencyId, cancellationToken);
        var lockout = await settings.GetAsync(CustomerAccountSettings.LockoutDuration, agencyId, cancellationToken);
        var lifetime = await settings.GetAsync(CustomerAccountSettings.RefreshTokenLifetime, agencyId, cancellationToken);
        var outcome = await unitOfWork.ExecuteGuardedAsync<Refusal<CustomerSession>>(
            async ct =>
            {
                var now = clock.GetUtcNow();
                var account = await db.Set<CustomerAccount>().FirstOrDefaultAsync(a => a.AgencyId == agencyId && a.Email == email, ct);
                if (account is null || account.IsClosed)
                {
                    // Same cost as a real check: an unknown address is not told apart by the response time.
                    hasher.Verify(_dummyHash ??= hasher.Hash(Guid.NewGuid().ToString("N")), password);
                    return Refusal<CustomerSession>.Of(CustomerAccountErrors.CredentialsInvalid);
                }

                if (account.IsLockedAt(now))
                {
                    return Refusal<CustomerSession>.Of(CustomerAccountErrors.Locked);
                }

                if (!hasher.Verify(account.PasswordHash, password))
                {
                    var locked = account.RecordFailedSignIn(now, maxFailures, lockout);
                    if (locked)
                    {
                        LogLocked(account.Id);
                    }

                    return Refusal<CustomerSession>.Of(locked ? CustomerAccountErrors.Locked : CustomerAccountErrors.CredentialsInvalid);
                }

                account.RecordSignIn(now);
                var (token, clear) = NewRefreshToken(account, Guid.CreateVersion7(), request.Device, now, lifetime);
                db.Set<CustomerRefreshToken>().Add(token);
                return Refusal<CustomerSession>.Accepted(new CustomerSession(Map(account), clear, token.ExpiresAtUtc));
            },
            cancellationToken);
        return Unwrap(outcome);
    }

    /// <summary>
    /// Exchanges a refresh token for a new one of the same family (rotation). A token presented again after its use is
    /// a replay: the whole family is revoked (committed) and the application signs in again.
    /// </summary>
    public async Task<Result<CustomerSession>> RefreshAsync(string? refreshToken, string? device = null, CancellationToken cancellationToken = default)
    {
        if (user.AgencyId is not { } agencyId)
        {
            return CustomerAccountErrors.AgencyRequired;
        }

        if (!AccountSecrets.IsWellFormedToken(refreshToken))
        {
            return CustomerAccountErrors.RefreshTokenInvalid;
        }

        var hash = AccountSecrets.HashToken(refreshToken!);
        var lifetime = await settings.GetAsync(CustomerAccountSettings.RefreshTokenLifetime, agencyId, cancellationToken);
        var outcome = await unitOfWork.ExecuteGuardedAsync<Refusal<CustomerSession>>(
            async ct =>
            {
                var now = clock.GetUtcNow();
                var existing = await db.Set<CustomerRefreshToken>().FirstOrDefaultAsync(t => t.AgencyId == agencyId && t.TokenHash.SequenceEqual(hash), ct);
                if (existing is null)
                {
                    return Refusal<CustomerSession>.Of(CustomerAccountErrors.RefreshTokenInvalid);
                }

                if (!existing.IsActiveAt(now))
                {
                    if (existing.UsedAtUtc is not null && existing.RevokedAtUtc is null)
                    {
                        await RevokeFamilyAsync(existing.Family, now, ct);
                        LogReplay(existing.AccountId);
                    }

                    return Refusal<CustomerSession>.Of(CustomerAccountErrors.RefreshTokenInvalid);
                }

                var account = await db.Set<CustomerAccount>().FirstOrDefaultAsync(a => a.Id == existing.AccountId, ct);
                if (account is null || account.IsClosed)
                {
                    existing.Revoke(now);
                    return Refusal<CustomerSession>.Of(CustomerAccountErrors.RefreshTokenInvalid);
                }

                existing.MarkUsed(now);
                var (token, clear) = NewRefreshToken(account, existing.Family, device ?? existing.Device, now, lifetime);
                db.Set<CustomerRefreshToken>().Add(token);
                return Refusal<CustomerSession>.Accepted(new CustomerSession(Map(account), clear, token.ExpiresAtUtc));
            },
            cancellationToken);
        return Unwrap(outcome);
    }

    /// <summary>Revokes a refresh token and its family (the application signs out); an unknown token is ignored.</summary>
    public async Task<Result> SignOutAsync(string? refreshToken, CancellationToken cancellationToken = default)
    {
        if (user.AgencyId is not { } agencyId || !AccountSecrets.IsWellFormedToken(refreshToken))
        {
            return Result.Success();
        }

        var hash = AccountSecrets.HashToken(refreshToken!);
        return await unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                var token = await db.Set<CustomerRefreshToken>().FirstOrDefaultAsync(t => t.AgencyId == agencyId && t.TokenHash.SequenceEqual(hash), ct);
                if (token is not null && token.RevokedAtUtc is null)
                {
                    // The whole device signs out: the token in hand and the one it may have been rotated from.
                    await RevokeFamilyAsync(token.Family, clock.GetUtcNow(), ct);
                }

                return Result.Success();
            },
            cancellationToken);
    }

    /// <summary>Revokes every refresh token of the account (every device).</summary>
    public Task<Result> SignOutEverywhereAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                await RevokeAllAsync(accountId, clock.GetUtcNow(), ct);
                return Result.Success();
            },
            cancellationToken);

    public async Task<CustomerAccountInfo?> GetAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await db.Set<CustomerAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        return account is null || account.IsClosed ? null : Map(account);
    }

    public Task<Result<CustomerAccountInfo>> UpdateProfileAsync(Guid accountId, CustomerProfileUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteGuardedAsync<CustomerAccountInfo>(
            async ct =>
            {
                var account = await db.Set<CustomerAccount>().FirstOrDefaultAsync(a => a.Id == accountId, ct);
                if (account is null)
                {
                    return CustomerAccountErrors.NotFound;
                }

                var updated = account.UpdateProfile(new CustomerProfileDetails(request.FirstName, request.LastName, request.Phone, request.Culture), clock.GetUtcNow());
                return updated.IsFailure ? updated.Error : Map(account);
            },
            cancellationToken);
    }

    /// <summary>The code of the verification e-mail: the address is verified. A wrong code is counted (committed) towards the invalidation of the code.</summary>
    public async Task<Result<CustomerAccountInfo>> VerifyEmailAsync(Guid accountId, string? code, CancellationToken cancellationToken = default)
    {
        var outcome = await unitOfWork.ExecuteGuardedAsync<Refusal<CustomerAccountInfo>>(
            async ct =>
            {
                var account = await db.Set<CustomerAccount>().FirstOrDefaultAsync(a => a.Id == accountId, ct);
                if (account is null)
                {
                    return Refusal<CustomerAccountInfo>.Of(CustomerAccountErrors.NotFound);
                }

                if (account.IsEmailVerified)
                {
                    return Refusal<CustomerAccountInfo>.Of(CustomerAccountErrors.AlreadyVerified);
                }

                var now = clock.GetUtcNow();
                var used = await UseCodeAsync(account.Id, CustomerCodePurpose.EmailVerification, code, now, ct);
                if (used.IsFailure)
                {
                    return Refusal<CustomerAccountInfo>.Of(used.Error);
                }

                var verified = account.VerifyEmail(now);
                return verified.IsFailure ? Refusal<CustomerAccountInfo>.Of(verified.Error) : Refusal<CustomerAccountInfo>.Accepted(Map(account));
            },
            cancellationToken);
        return Unwrap(outcome);
    }

    /// <summary>A new verification code (the previous ones stop working).</summary>
    public async Task<Result> ResendVerificationAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var validity = await settings.GetAsync(CustomerAccountSettings.CodeValidity, user.AgencyId, cancellationToken);
        return await unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                var account = await db.Set<CustomerAccount>().FirstOrDefaultAsync(a => a.Id == accountId, ct);
                if (account is null || account.IsClosed)
                {
                    return CustomerAccountErrors.NotFound;
                }

                if (account.IsEmailVerified)
                {
                    return CustomerAccountErrors.AlreadyVerified;
                }

                var now = clock.GetUtcNow();
                await InvalidateCodesAsync(account.Id, CustomerCodePurpose.EmailVerification, now, ct);
                IssueCode(account, CustomerCodePurpose.EmailVerification, CustomerAccountEmailKind.EmailVerification, now, validity);
                return Result.Success();
            },
            cancellationToken);
    }

    /// <summary>
    /// A password reset asked for an address: when it has an open account, a code is e-mailed. The answer is the same
    /// whether it has one or not.
    /// </summary>
    public async Task<Result> ForgotPasswordAsync(string? email, CancellationToken cancellationToken = default)
    {
        if (user.AgencyId is not { } agencyId)
        {
            return CustomerAccountErrors.AgencyRequired;
        }

        var normalized = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 254)
        {
            return Result.Success();
        }

        var validity = await settings.GetAsync(CustomerAccountSettings.CodeValidity, agencyId, cancellationToken);
        return await unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                var account = await db.Set<CustomerAccount>().FirstOrDefaultAsync(a => a.AgencyId == agencyId && a.Email == normalized, ct);
                if (account is null || account.IsClosed)
                {
                    return Result.Success();
                }

                var now = clock.GetUtcNow();
                await InvalidateCodesAsync(account.Id, CustomerCodePurpose.PasswordReset, now, ct);
                IssueCode(account, CustomerCodePurpose.PasswordReset, CustomerAccountEmailKind.PasswordReset, now, validity);
                return Result.Success();
            },
            cancellationToken);
    }

    /// <summary>
    /// The code of the reset e-mail and a new password: the credentials change, every device signs out, the address counts
    /// as verified. A wrong code is counted (committed) towards the invalidation of the code.
    /// </summary>
    public async Task<Result> ResetPasswordAsync(string? email, string? code, string? newPassword, CancellationToken cancellationToken = default)
    {
        if (user.AgencyId is not { } agencyId)
        {
            return CustomerAccountErrors.AgencyRequired;
        }

        var normalized = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 254)
        {
            return CustomerAccountErrors.CodeInvalid;
        }

        var password = CustomerPasswords.Validate(newPassword, normalized);
        if (password.IsFailure)
        {
            return password.Error;
        }

        var outcome = await unitOfWork.ExecuteGuardedAsync<Refusal<bool>>(
            async ct =>
            {
                var account = await db.Set<CustomerAccount>().FirstOrDefaultAsync(a => a.AgencyId == agencyId && a.Email == normalized, ct);
                if (account is null || account.IsClosed)
                {
                    return Refusal<bool>.Of(CustomerAccountErrors.CodeInvalid);
                }

                var now = clock.GetUtcNow();
                var used = await UseCodeAsync(account.Id, CustomerCodePurpose.PasswordReset, code, now, ct);
                if (used.IsFailure)
                {
                    return Refusal<bool>.Of(used.Error);
                }

                var changed = account.ChangePassword(hasher.Hash(newPassword!), now);
                if (changed.IsFailure)
                {
                    return Refusal<bool>.Of(changed.Error);
                }

                if (!account.IsEmailVerified)
                {
                    account.VerifyEmail(now);
                }

                await RevokeAllAsync(account.Id, now, ct);
                return Refusal<bool>.Accepted(true);
            },
            cancellationToken);
        var result = Unwrap(outcome);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    /// <summary>The current and a new password: the credentials change, every other device signs out, the caller gets a new session.</summary>
    public async Task<Result<CustomerSession>> ChangePasswordAsync(Guid accountId, string? currentPassword, string? newPassword, string? device = null, CancellationToken cancellationToken = default)
    {
        var lifetime = await settings.GetAsync(CustomerAccountSettings.RefreshTokenLifetime, user.AgencyId, cancellationToken);
        return await unitOfWork.ExecuteGuardedAsync<CustomerSession>(
            async ct =>
            {
                var account = await db.Set<CustomerAccount>().FirstOrDefaultAsync(a => a.Id == accountId, ct);
                if (account is null || account.IsClosed)
                {
                    return CustomerAccountErrors.NotFound;
                }

                if (string.IsNullOrEmpty(currentPassword) || !hasher.Verify(account.PasswordHash, currentPassword))
                {
                    return CustomerAccountErrors.CredentialsInvalid;
                }

                var password = CustomerPasswords.Validate(newPassword, account.Email);
                if (password.IsFailure)
                {
                    return password.Error;
                }

                var now = clock.GetUtcNow();
                var changed = account.ChangePassword(hasher.Hash(newPassword!), now);
                if (changed.IsFailure)
                {
                    return changed.Error;
                }

                await RevokeAllAsync(account.Id, now, ct);
                var (token, clear) = NewRefreshToken(account, Guid.CreateVersion7(), device, now, lifetime);
                db.Set<CustomerRefreshToken>().Add(token);
                return new CustomerSession(Map(account), clear, token.ExpiresAtUtc);
            },
            cancellationToken);
    }

    /// <summary>Closes the account (password confirmed): scrubbed, every device signed out; bookings and the person stay.</summary>
    public Task<Result> CloseAsync(Guid accountId, string? password, CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                var account = await db.Set<CustomerAccount>().FirstOrDefaultAsync(a => a.Id == accountId, ct);
                if (account is null || account.IsClosed)
                {
                    return CustomerAccountErrors.NotFound;
                }

                if (string.IsNullOrEmpty(password) || !hasher.Verify(account.PasswordHash, password))
                {
                    return CustomerAccountErrors.CredentialsInvalid;
                }

                var now = clock.GetUtcNow();
                await RevokeAllAsync(account.Id, now, ct);
                account.Close(now);
                LogClosed(account.Id);
                return Result.Success();
            },
            cancellationToken);

    internal static CustomerAccountInfo Map(CustomerAccount account) => new(
        account.Id,
        account.AgencyId,
        account.Email,
        account.IsEmailVerified,
        account.FirstName,
        account.LastName,
        account.Phone,
        account.Culture,
        account.PersonId,
        account.SecurityStamp,
        account.CreatedAtUtc);

    /// <summary>The business answer of a committed unit of work: the refusal it recorded, or the value.</summary>
    private static Result<T> Unwrap<T>(Result<Refusal<T>> outcome)
    {
        if (outcome.IsFailure)
        {
            return outcome.Error;
        }

        return outcome.Value.Error is { } refused ? refused : outcome.Value.Value!;
    }

    private (CustomerRefreshToken Token, string Clear) NewRefreshToken(CustomerAccount account, Guid family, string? device, DateTimeOffset now, TimeSpan lifetime)
    {
        var clear = AccountSecrets.NewToken();
        return (CustomerRefreshToken.Issue(account.AgencyId, account.Id, AccountSecrets.HashToken(clear), family, device, user.IpAddress, now, lifetime), clear);
    }

    /// <summary>Issues a code for the account and queues its e-mail (committed together with the code).</summary>
    private void IssueCode(CustomerAccount account, CustomerCodePurpose purpose, CustomerAccountEmailKind kind, DateTimeOffset now, TimeSpan validity)
    {
        var (entity, code) = CustomerAccountCode.Issue(account.AgencyId, account.Id, purpose, now, validity);
        db.Set<CustomerAccountCode>().Add(entity);
        outbox.Enqueue(new CustomerAccountEmailRequested(new CustomerAccountEmail(kind, account.AgencyId, account.Email, account.FullName, account.Culture, code, entity.ExpiresAtUtc)));
    }

    /// <summary>Checks the typed code against the usable codes of the purpose (the newest first).</summary>
    private async Task<Result> UseCodeAsync(Guid accountId, CustomerCodePurpose purpose, string? code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var candidates = await db.Set<CustomerAccountCode>()
            .Where(c => c.AccountId == accountId && c.Purpose == purpose && c.UsedAtUtc == null && c.InvalidatedAtUtc == null && c.ExpiresAtUtc > now)
            .OrderByDescending(c => c.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return CustomerAccountErrors.CodeInvalid;
        }

        // Only the newest code counts: a wrong attempt is charged to it (five attempts, then a new code is needed).
        return candidates[0].TryUse(code, now);
    }

    private async Task InvalidateCodesAsync(Guid accountId, CustomerCodePurpose purpose, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var previous = await db.Set<CustomerAccountCode>()
            .Where(c => c.AccountId == accountId && c.Purpose == purpose && c.UsedAtUtc == null && c.InvalidatedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var code in previous)
        {
            code.Invalidate(now);
        }
    }

    private async Task RevokeFamilyAsync(Guid family, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tokens = await db.Set<CustomerRefreshToken>().Where(t => t.Family == family && t.RevokedAtUtc == null).ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.Revoke(now);
        }
    }

    private async Task RevokeAllAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tokens = await db.Set<CustomerRefreshToken>().Where(t => t.AccountId == accountId && t.RevokedAtUtc == null && t.ExpiresAtUtc > now).ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.Revoke(now);
        }
    }

    /// <summary>What a committed unit of work answers: a value, or a refusal whose trace was committed with it.</summary>
    private sealed record Refusal<T>(T? Value, Error? Error)
    {
        public static Refusal<T> Accepted(T value) => new(value, null);

        public static Refusal<T> Of(Error error) => new(default, error);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Customer account {AccountId} opened for agency {AgencyId}.")]
    private partial void LogRegistered(Guid accountId, Guid agencyId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Customer account {AccountId} locked after too many failed sign-ins.")]
    private partial void LogLocked(Guid accountId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A used refresh token of customer account {AccountId} was presented again: its family is revoked.")]
    private partial void LogReplay(Guid accountId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Customer account {AccountId} closed at the request of the customer.")]
    private partial void LogClosed(Guid accountId);
}
