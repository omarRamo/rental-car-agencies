using CarRental.Application.Customers.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;

namespace CarRental.Application.Customers;

/// <summary>Conversions between the domain types of the module and its contract types (same enum values).</summary>
internal static class CustomerMapping
{
    /// <summary>Visible characters of a masked document number.</summary>
    private const int VisibleCharacters = 4;

    public static IdentityDocumentType ToDomain(this IdentityDocumentTypeInfo type) => (IdentityDocumentType)(int)type;

    public static IdentityDocumentTypeInfo ToInfo(this IdentityDocumentType type) => (IdentityDocumentTypeInfo)(int)type;

    public static DocumentImageSide ToDomain(this DocumentImageSideInfo side) => (DocumentImageSide)(int)side;

    public static ConsentPurpose ToDomain(this ConsentPurposeInfo purpose) => (ConsentPurpose)(int)purpose;

    public static ConsentPurposeInfo ToInfo(this ConsentPurpose purpose) => (ConsentPurposeInfo)(int)purpose;

    public static ConsentChannel ToDomain(this ConsentChannelInfo channel) => (ConsentChannel)(int)channel;

    public static ConsentChannelInfo ToInfo(this ConsentChannel channel) => (ConsentChannelInfo)(int)channel;

    public static CustomerKindInfo ToInfo(this CustomerKind kind) => (CustomerKindInfo)(int)kind;

    public static EligibilityIssueKindInfo ToInfo(this EligibilityIssueKind kind) => (EligibilityIssueKindInfo)(int)kind;

    public static IdentityDocumentDetails ToDomain(this IdentityDocumentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new IdentityDocumentDetails(input.Type.ToDomain(), input.Number, input.IssuingCountry, input.IssuedOn, input.ExpiresOn, input.IsLatinScript);
    }

    /// <summary>The domain address of <paramref name="address"/> (validated), or null for no address.</summary>
    public static Result<PostalAddress?> ToDomain(this PostalAddressInfo? address)
    {
        if (address is null)
        {
            return Result.Success<PostalAddress?>(null);
        }

        var created = PostalAddress.Create(address.Line1, address.Line2, address.PostalCode, address.City, address.Country);
        return created.IsSuccess ? Result.Success<PostalAddress?>(created.Value) : Result.Failure<PostalAddress?>(created.Error);
    }

    public static PostalAddressInfo? ToInfo(this PostalAddress? address) =>
        address is null ? null : new PostalAddressInfo(address.Line1, address.Line2, address.PostalCode, address.City, address.Country);

    public static Result<PersonDetails> ToDomain(
        string firstName,
        string lastName,
        string? nationality,
        DateOnly? birthDate,
        string? phone,
        string? email,
        PostalAddressInfo? address)
    {
        var postalAddress = address.ToDomain();
        if (postalAddress.IsFailure)
        {
            return postalAddress.Error;
        }

        return new PersonDetails(firstName, lastName, nationality, birthDate, phone, email, postalAddress.Value);
    }

    public static Result<CompanyDetails> ToDomain(this CompanyCustomerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BillingAddress is null)
        {
            return CustomersErrors.BillingAddressRequired;
        }

        var address = request.BillingAddress.ToDomain();
        if (address.IsFailure)
        {
            return address.Error;
        }

        return new CompanyDetails(request.LegalName, request.TaxIdentifier, address.Value!, request.ContactName, request.Email, request.Phone);
    }

    /// <summary>The number itself, or its last characters behind bullets for users who may not read identity documents.</summary>
    public static string MaskNumber(string number, bool reveal)
    {
        if (reveal || number.Length <= VisibleCharacters)
        {
            return reveal ? number : new string('•', number.Length);
        }

        return new string('•', number.Length - VisibleCharacters) + number[^VisibleCharacters..];
    }

    public static IdentityDocumentInfo ToInfo(this IdentityDocument document, bool revealNumber, string? verifiedBy = null) => new(
        document.Id,
        document.Type.ToInfo(),
        MaskNumber(document.Number, revealNumber),
        document.IssuingCountry,
        document.IssuedOn,
        document.ExpiresOn,
        document.IsLatinScript,
        document.FrontImage is not null,
        document.BackImage is not null,
        document.VerifiedAtUtc,
        verifiedBy ?? document.VerifiedByName);

    public static EligibilityIssueInfo ToInfo(this EligibilityIssue issue) =>
        new(issue.Kind.ToInfo(), issue.DocumentType?.ToInfo(), issue.Date, issue.Required, issue.Actual);

    /// <summary>Stable kind of a document side for the access log, e.g. <c>passport.front</c>.</summary>
    public static string AccessLogKind(IdentityDocumentType type, DocumentImageSide side)
    {
        var kind = type switch
        {
            IdentityDocumentType.NationalId => "identity_card",
            IdentityDocumentType.Passport => "passport",
            IdentityDocumentType.DrivingLicense => "driving_licence",
            IdentityDocumentType.InternationalDrivingPermit => "international_driving_permit",
            _ => "identity_document",
        };
        return side == DocumentImageSide.Back ? kind + ".back" : kind + ".front";
    }
}
