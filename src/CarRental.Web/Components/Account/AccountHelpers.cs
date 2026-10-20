using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using CarRental.Application.Common.Auditing;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Identity;
using CarRental.Web.Resources.Account;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;
using QRCoder;

namespace CarRental.Web.Components.Account;

/// <summary>Authenticator enrollment: shared key formatting, otpauth URI and its QR code (PNG data URI, allowed by the CSP).</summary>
internal static class AuthenticatorSetup
{
    public const string Issuer = "CarRental";

    public static string FormatKey(string unformattedKey)
    {
        ArgumentNullException.ThrowIfNull(unformattedKey);
        var result = new StringBuilder();
        for (var position = 0; position < unformattedKey.Length; position += 4)
        {
            if (position > 0)
            {
                result.Append(' ');
            }

            result.Append(unformattedKey.AsSpan(position, Math.Min(4, unformattedKey.Length - position)));
        }

        return result.ToString().ToLowerInvariant();
    }

    public static string BuildUri(string email, string unformattedKey) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"otpauth://totp/{UrlEncoder.Default.Encode(Issuer)}:{UrlEncoder.Default.Encode(email)}?secret={unformattedKey}&issuer={UrlEncoder.Default.Encode(Issuer)}&digits=6");

    public static string QrCodeDataUri(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(8);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    /// <summary>Removes spaces and hyphens typed with the code.</summary>
    public static string NormalizeCode(string? code) =>
        (code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
}

/// <summary>Bookkeeping of a completed sign-in: last sign-in date and the audit event.</summary>
internal sealed class SignInRecorder(UserManager<ApplicationUser> userManager, IAuditTrail auditTrail, TimeProvider timeProvider)
{
    public async Task SucceededAsync(ApplicationUser user, string method)
    {
        ArgumentNullException.ThrowIfNull(user);
        user.LastSignInAtUtc = timeProvider.GetUtcNow();
        await userManager.UpdateAsync(user);
        await auditTrail.RecordAsync(new AuditEvent(AuditActions.LoginSucceeded)
        {
            EntityType = "User",
            EntityId = user.Id.ToString(),
            Details = method,
            ActingUserId = user.Id,
            ActingUserName = user.UserName,
        });
    }

    public Task FailedAsync(ApplicationUser? user, string reason, string? attemptedEmail = null) =>
        auditTrail.RecordAsync(new AuditEvent(AuditActions.LoginFailed)
        {
            EntityType = "User",
            EntityId = user?.Id.ToString(),
            Details = user is null ? $"{reason} ({attemptedEmail})" : reason,
        });

    public Task LockedOutAsync(ApplicationUser user) =>
        auditTrail.RecordAsync(new AuditEvent(AuditActions.LockedOut)
        {
            EntityType = "User",
            EntityId = user.Id.ToString(),
            ActingUserId = user.Id,
            ActingUserName = user.UserName,
        });

    public Task AccountEventAsync(string action, ClaimsPrincipal principal, string? details = null) =>
        auditTrail.RecordAsync(new AuditEvent(action)
        {
            EntityType = "User",
            EntityId = principal.GetUserId()?.ToString(),
            Details = details,
        });
}

/// <summary>Identity validation messages in the user's language (password rules, invalid token, duplicates).</summary>
internal sealed class LocalizedIdentityErrorDescriber(IStringLocalizer<AccountResources> localizer) : IdentityErrorDescriber
{
    public override IdentityError DefaultError() => Error(nameof(DefaultError));

    public override IdentityError InvalidToken() => Error(nameof(InvalidToken));

    public override IdentityError PasswordMismatch() => Error(nameof(PasswordMismatch));

    public override IdentityError PasswordTooShort(int length) => Error(nameof(PasswordTooShort), length);

    public override IdentityError PasswordRequiresDigit() => Error(nameof(PasswordRequiresDigit));

    public override IdentityError PasswordRequiresLower() => Error(nameof(PasswordRequiresLower));

    public override IdentityError PasswordRequiresUpper() => Error(nameof(PasswordRequiresUpper));

    public override IdentityError PasswordRequiresNonAlphanumeric() => Error(nameof(PasswordRequiresNonAlphanumeric));

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => Error(nameof(PasswordRequiresUniqueChars), uniqueChars);

    public override IdentityError DuplicateEmail(string email) => Error(nameof(DuplicateEmail));

    public override IdentityError DuplicateUserName(string userName) => Error(nameof(DuplicateUserName));

    public override IdentityError InvalidEmail(string? email) => Error(nameof(InvalidEmail));

    public override IdentityError UserLockoutNotEnabled() => Error(nameof(UserLockoutNotEnabled));

    public override IdentityError RecoveryCodeRedemptionFailed() => Error(nameof(RecoveryCodeRedemptionFailed));

    private IdentityError Error(string code, params object[] arguments) =>
        new() { Code = code, Description = localizer[$"IdentityError.{code}", arguments] };
}
