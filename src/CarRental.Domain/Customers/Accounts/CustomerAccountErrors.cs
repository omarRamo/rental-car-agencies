using CarRental.Domain.Common;

namespace CarRental.Domain.Customers.Accounts;

/// <summary>Business errors of the customer accounts (codes <c>customers.account.*</c>, texts <c>Error.{code}</c> in the web resources).</summary>
public static class CustomerAccountErrors
{
    public static readonly Error AgencyRequired = Error.Forbidden(
        "customers.account.agency_required",
        "A customer account belongs to the agency of the site or application it was created on.");

    public static readonly Error EmailTaken = Error.Conflict(
        "customers.account.email_taken",
        "An account with this e-mail address already exists for this agency.");

    public static readonly Error PasswordWeak = Error.Validation(
        "customers.account.password_weak",
        "The password must have between 10 and 128 characters and must not be a common password.");

    /// <summary>Unknown e-mail, wrong password or closed account: always the same answer.</summary>
    public static readonly Error CredentialsInvalid = Error.Validation(
        "customers.account.credentials_invalid",
        "The e-mail address or the password is incorrect.");

    public static readonly Error Locked = Error.Conflict(
        "customers.account.locked",
        "Too many failed sign-in attempts: the account is locked for a few minutes.");

    public static readonly Error NotFound = Error.NotFound(
        "customers.account.not_found",
        "The customer account does not exist.");

    public static readonly Error Closed = Error.Conflict(
        "customers.account.closed",
        "The customer account was closed.");

    public static readonly Error EmailNotVerified = Error.Forbidden(
        "customers.account.email_not_verified",
        "The e-mail address of the account must be verified first.");

    public static readonly Error AlreadyVerified = Error.Conflict(
        "customers.account.already_verified",
        "The e-mail address is already verified.");

    /// <summary>Unknown, expired, used or wrong code (or unknown e-mail for a reset): always the same answer.</summary>
    public static readonly Error CodeInvalid = Error.Validation(
        "customers.account.code_invalid",
        "The code is not valid (or no longer): ask for a new one.");

    /// <summary>Unknown, expired, revoked or already used refresh token: the application signs in again.</summary>
    public static readonly Error RefreshTokenInvalid = Error.Validation(
        "customers.account.refresh_token_invalid",
        "The session has expired: sign in again.");

    public static readonly Error NameInvalid = Error.Validation(
        "customers.account.name_invalid",
        "The first name and the last name are required (letters only, 100 characters at most).");

    public static readonly Error CultureInvalid = Error.Validation(
        "customers.account.culture_invalid",
        "The language must be fr or en.");

    public static readonly Error BookingNotFound = Error.NotFound(
        "customers.account.booking_not_found",
        "This booking does not belong to the account.");

    public static readonly Error TermsRequired = Error.Validation(
        "customers.account.terms_required",
        "The rental terms must be accepted.");
}
