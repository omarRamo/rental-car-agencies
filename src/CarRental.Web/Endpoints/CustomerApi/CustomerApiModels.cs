using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.PublicAccess;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Components.Public.Site;
using CarRental.Web.Endpoints.PublicSite;

namespace CarRental.Web.Endpoints.CustomerApi;

/// <summary>The site of an agency as the application presents it: identity, contact, locations, categories, policies.</summary>
public sealed record CustomerApiSite(
    string Slug,
    string Name,
    string City,
    string? Address,
    string? Phone,
    string? Email,
    decimal? Latitude,
    decimal? Longitude,
    string? WhatsAppNumber,
    string OpeningHours,
    IReadOnlyList<string> Cultures,
    string Currency,
    decimal? EurExchangeRate,
    CustomerApiPolicies Policies,
    IReadOnlyList<PublicLocationDto> Locations,
    IReadOnlyList<CustomerApiCategory> Categories,
    IReadOnlyDictionary<string, string> TermsUrls,
    IReadOnlyDictionary<string, string> PrivacyUrls);

/// <summary>The rental conditions the application states before a booking.</summary>
public sealed record CustomerApiPolicies(int DriverMinimumAge, int DriverMinimumLicenseYears, int YoungDriverAgeBelow, bool EmailMustBeVerifiedToBook);

/// <summary>A category of the fleet (its features and deposit; prices come from the availability and quote endpoints).</summary>
public sealed record CustomerApiCategory(
    string Code,
    string Name,
    string ExampleModel,
    int Seats,
    int Doors,
    int LargeBags,
    int SmallBags,
    string Transmission,
    string Fuel,
    bool AirConditioning,
    decimal Deposit,
    decimal ReducedDeposit,
    string? PhotoUrl)
{
    internal static CustomerApiCategory From(VehicleCategoryInfo category, Uri baseUri) => new(
        category.AcrissCode,
        category.Name,
        category.ExampleModel,
        category.Seats,
        category.Doors,
        category.LargeBags,
        category.SmallBags,
        category.Transmission == TransmissionInfo.Automatic ? "automatic" : "manual",
        category.Fuel switch
        {
            FuelTypeInfo.Diesel => "diesel",
            FuelTypeInfo.Hybrid => "hybrid",
            FuelTypeInfo.Electric => "electric",
            FuelTypeInfo.Lpg => "lpg",
            _ => "petrol",
        },
        category.AirConditioning,
        category.Deposit.Amount,
        category.ReducedDeposit.Amount,
        category.PhotoUrl is { } photo ? PublicUrls.Absolute(baseUri, photo).AbsoluteUri : null);
}

// Authentication

public sealed record CustomerApiRegisterRequest(string? Email, string? Password, string? FirstName, string? LastName, string? Phone, string? Culture, string? Device);

public sealed record CustomerApiLoginRequest(string? Email, string? Password, string? Device);

public sealed record CustomerApiRefreshRequest(string? RefreshToken, string? Device);

public sealed record CustomerApiLogoutRequest(string? RefreshToken);

public sealed record CustomerApiForgotPasswordRequest(string? Email);

public sealed record CustomerApiResetPasswordRequest(string? Email, string? Code, string? NewPassword);

public sealed record CustomerApiChangePasswordRequest(string? CurrentPassword, string? NewPassword, string? Device);

public sealed record CustomerApiCodeRequest(string? Code);

public sealed record CustomerApiCloseAccountRequest(string? Password);

public sealed record CustomerApiProfileRequest(string? FirstName, string? LastName, string? Phone, string? Culture);

/// <summary>The account of the customer (never the password).</summary>
public sealed record CustomerApiAccount(Guid Id, string Email, bool EmailVerified, string FirstName, string LastName, string? Phone, string Culture, DateTimeOffset CreatedAtUtc)
{
    internal static CustomerApiAccount From(CustomerAccountInfo account) =>
        new(account.Id, account.Email, account.EmailVerified, account.FirstName, account.LastName, account.Phone, account.Culture, account.CreatedAtUtc);
}

/// <summary>A session: the access token (bearer, short), the refresh token (long, single use) and the account.</summary>
public sealed record CustomerApiSession(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc,
    CustomerApiAccount Account)
{
    public string TokenType => "Bearer";

    internal static CustomerApiSession From(CustomerSession session, IssuedAccessToken access) =>
        new(access.Token, access.ExpiresAtUtc, session.RefreshToken, session.RefreshTokenExpiresAtUtc, CustomerApiAccount.From(session.Account));
}

// Bookings

/// <summary>A driver as typed in the application; the main driver takes the names, e-mail and phone of the account when left empty.</summary>
public sealed record CustomerApiDriver(
    string? FirstName = null,
    string? LastName = null,
    string? Nationality = null,
    string? BirthDate = null,
    string? Phone = null,
    string? Email = null,
    string? LicenseNumber = null,
    string? LicenseIssuedOn = null,
    string? LicenseExpiresOn = null,
    string? LicenseCountry = null,
    bool LicenseLatinScript = true,
    string? IdentityNumber = null,
    string? IdentityCountry = null,
    string? IdentityExpiresOn = null,
    string? PermitNumber = null,
    string? PermitCountry = null,
    string? PermitExpiresOn = null)
{
    internal FunnelDriverInput ToInput(CustomerIdentity? defaults = null) => new(
        Fallback(FirstName, defaults?.Name.Split(' ', 2).FirstOrDefault()),
        Fallback(LastName, defaults?.Name.Split(' ', 2).Skip(1).FirstOrDefault()),
        Nationality?.Trim().ToUpperInvariant(),
        BirthDate,
        Fallback(Phone, defaults?.Phone),
        Fallback(Email, defaults?.Email),
        LicenseNumber,
        LicenseIssuedOn,
        LicenseExpiresOn,
        LicenseCountry?.Trim().ToUpperInvariant(),
        LicenseLatinScript,
        IdentityNumber,
        IdentityCountry?.Trim().ToUpperInvariant(),
        IdentityExpiresOn,
        PermitNumber,
        PermitCountry?.Trim().ToUpperInvariant(),
        PermitExpiresOn);

    private static string? Fallback(string? typed, string? fallback) => string.IsNullOrWhiteSpace(typed) ? fallback : typed;
}

/// <summary>The consents of a booking request (the processing of the identity documents is mandatory).</summary>
public sealed record CustomerApiConsents(bool Documents, bool WhatsApp = false, bool Marketing = false);

/// <summary>
/// A booking request: the rental (same fields as the quote API), an optional quote code to reuse, the extras, the
/// drivers, the consents and the acceptance of the terms.
/// </summary>
public sealed record CustomerApiBookingRequest(
    string? PickupLocation,
    string? ReturnLocation,
    string? Pickup,
    string? Return,
    string? Category,
    string? QuoteCode,
    IReadOnlyList<PublicQuoteExtra>? Extras,
    CustomerApiDriver? MainDriver,
    CustomerApiDriver? SecondDriver,
    CustomerApiConsents? Consents,
    bool TermsAccepted,
    string? Culture);

/// <summary>How the request is guaranteed: a pre-authorization on the payment page of the bank, or a payment link after the validation.</summary>
public sealed record CustomerApiGuarantee(string Mode, decimal Amount);

/// <summary>
/// A request created: its number, where it stands, its guarantee and, for a pre-authorization, the payment page of the
/// bank to open in a browser (the return leads to <paramref name="ConfirmationUrl"/>, which the application watches for).
/// </summary>
public sealed record CustomerApiBookingCreated(
    Guid BookingId,
    string Number,
    string Status,
    decimal TotalInclVat,
    string Currency,
    CustomerApiGuarantee Guarantee,
    string? PaymentUrl,
    DateTimeOffset? PaymentExpiresAtUtc,
    string ConfirmationUrl);

/// <summary>A booking in the list of the customer.</summary>
public sealed record CustomerApiBookingSummary(
    Guid Id,
    string Number,
    string Status,
    string Channel,
    string CategoryCode,
    string? PickupLocation,
    string? ReturnLocation,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    decimal TotalInclVat,
    decimal Deposit,
    string Currency,
    DateTimeOffset CreatedAtUtc);

public sealed record CustomerApiTimelineEntry(string Type, DateTimeOffset OccurredAtUtc);

public sealed record CustomerApiSlot(string Key, string Document, string Side, bool Received, bool Optional);

public sealed record CustomerApiBookingDriver(string Role, string Name, string IdentityType, IReadOnlyList<CustomerApiSlot> Slots);

public sealed record CustomerApiCancellation(decimal Fee, DateOnly? FreeUntil, decimal FeePercent, bool IsFree, decimal RefundEstimate);

public sealed record CustomerApiDocument(string Id, string Kind, string? Number, DateTimeOffset IssuedAtUtc, string Url);

/// <summary>A booking as the customer follows it in the application.</summary>
public sealed record CustomerApiBookingDetails(
    Guid Id,
    string Number,
    string Status,
    string Channel,
    string CategoryCode,
    string? CategoryName,
    PublicLocationDto? PickupLocation,
    PublicLocationDto? ReturnLocation,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    decimal TotalInclVat,
    decimal Deposit,
    decimal Paid,
    decimal Balance,
    string Currency,
    DateTimeOffset? HoldExpiresAt,
    DateTimeOffset? PaymentDueAt,
    string? InformationMessage,
    string PaymentState,
    IReadOnlyList<CustomerApiTimelineEntry> Timeline,
    IReadOnlyList<CustomerApiBookingDriver> Drivers,
    bool CanSendDocuments,
    bool CanProvideInformation,
    bool CanPayBalance,
    CustomerApiCancellation? Cancellation,
    IReadOnlyList<CustomerApiDocument> Documents,
    string Culture);

public sealed record CustomerApiCancelRequest(decimal? ExpectedFee);

public sealed record CustomerApiCancelled(decimal Fee, decimal RefundAmount, string Refund);

/// <summary>The payment page of the bank to open in a browser for the balance.</summary>
public sealed record CustomerApiPaymentRedirect(Guid PaymentId, string PaymentUrl, DateTimeOffset ExpiresAtUtc);

public sealed record CustomerApiUploadReceived(string Slot, string ContentType, long Length);

internal static class CustomerApiNames
{
    public static string Status(BookingStatusInfo status) => status switch
    {
        BookingStatusInfo.Draft => "draft",
        BookingStatusInfo.PendingValidation => "pending_validation",
        BookingStatusInfo.InformationRequested => "information_requested",
        BookingStatusInfo.OptionHeld => "option_held",
        BookingStatusInfo.AwaitingPayment => "awaiting_payment",
        BookingStatusInfo.Confirmed => "confirmed",
        BookingStatusInfo.InProgress => "in_progress",
        BookingStatusInfo.Returned => "returned",
        BookingStatusInfo.Closed => "closed",
        BookingStatusInfo.Refused => "refused",
        BookingStatusInfo.Expired => "expired",
        BookingStatusInfo.Cancelled => "cancelled",
        BookingStatusInfo.NoShow => "no_show",
        _ => status.ToString().ToLowerInvariant(),
    };

    public static string Channel(SalesChannel channel) => channel.ToString().ToLowerInvariant();

    public static string Guarantee(FunnelGuaranteeMode mode) => mode == FunnelGuaranteeMode.PreAuthorization ? "pre_authorization" : "payment_link_after_validation";

    public static string Snake(Enum value) =>
        string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
