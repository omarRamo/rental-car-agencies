using System.ComponentModel.DataAnnotations.Schema;
using CarRental.Domain.Common;

namespace CarRental.Domain.Billing;

/// <summary>Who performed a billing operation: the user (or <see langword="null"/> for the platform) and the name kept with the record.</summary>
public sealed record BillingActor
{
    public const int NameMaxLength = 256;

    public const string SystemName = "system";

    public BillingActor(Guid? userId, string? name)
    {
        UserId = userId;
        var clean = string.IsNullOrWhiteSpace(name) ? SystemName : name.Trim();
        Name = clean.Length > NameMaxLength ? clean[..NameMaxLength] : clean;
    }

    public Guid? UserId { get; }

    public string Name { get; }

    public static BillingActor System { get; } = new(null, SystemName);
}

/// <summary>
/// The seller printed on a billing document, frozen at its issue (the agency may change its details later): legal name,
/// tax identifier (matricule fiscal), trade register number, legal address, contact and the footer of its invoices.
/// </summary>
/// <remarks>
/// TODO(spec): the format of the matricule fiscal is checked loosely (points-ouverts.md); the final list of the mandatory
/// mentions comes from the chartered accountant.
/// </remarks>
[ComplexType]
public sealed record SellerIdentity
{
    public const int LegalNameMaxLength = 200;
    public const int TaxIdentifierMaxLength = 32;
    public const int TradeRegisterMaxLength = 64;
    public const int AddressMaxLength = 400;
    public const int PhoneMaxLength = 32;
    public const int EmailMaxLength = 254;
    public const int FooterMaxLength = 1000;

    private SellerIdentity(string legalName, string taxIdentifier, string? tradeRegisterNumber, string address, string? phone, string? email, string? footer)
    {
        LegalName = legalName;
        TaxIdentifier = taxIdentifier;
        TradeRegisterNumber = tradeRegisterNumber;
        Address = address;
        Phone = phone;
        Email = email;
        Footer = footer;
    }

    public string LegalName { get; private init; }

    /// <summary>Matricule fiscal of the agency.</summary>
    public string TaxIdentifier { get; private init; }

    /// <summary>Trade register (RNE) number, when known.</summary>
    public string? TradeRegisterNumber { get; private init; }

    /// <summary>Legal address (head office), on one or several lines separated by commas.</summary>
    public string Address { get; private init; }

    public string? Phone { get; private init; }

    public string? Email { get; private init; }

    /// <summary>Free text printed at the bottom of the documents (capital, bank details…).</summary>
    public string? Footer { get; private init; }

    /// <summary>
    /// The seller of a document: legal name, tax identifier and address are mandatory (no invoice is issued without them,
    /// <see cref="BillingErrors.SellerIdentityIncomplete"/>).
    /// </summary>
    public static Result<SellerIdentity> Create(
        string? legalName,
        string? taxIdentifier,
        string? tradeRegisterNumber,
        string? address,
        string? phone = null,
        string? email = null,
        string? footer = null)
    {
        var name = Clean(legalName);
        var tax = Clean(taxIdentifier);
        var place = Clean(address);
        if (name is null || tax is null || place is null)
        {
            return BillingErrors.SellerIdentityIncomplete;
        }

        var register = Clean(tradeRegisterNumber);
        var phoneNumber = Clean(phone);
        var mail = Clean(email);
        var text = Clean(footer);
        if (name.Length > LegalNameMaxLength
            || tax.Length > TaxIdentifierMaxLength
            || register?.Length > TradeRegisterMaxLength
            || place.Length > AddressMaxLength
            || phoneNumber?.Length > PhoneMaxLength
            || mail?.Length > EmailMaxLength
            || text?.Length > FooterMaxLength)
        {
            return BillingErrors.PartyFieldTooLong;
        }

        return new SellerIdentity(name, tax.ToUpperInvariant(), register, place, phoneNumber, mail, text);
    }

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The buyer printed on a billing document, frozen at its issue: the payer of the file — a person, or a company with its
/// tax identifier and billing address — and the e-mail address the document is sent to.
/// </summary>
[ComplexType]
public sealed record BuyerIdentity
{
    public const int NameMaxLength = 200;
    public const int TaxIdentifierMaxLength = 32;
    public const int AddressLineMaxLength = 200;
    public const int PostalCodeMaxLength = 16;
    public const int CityMaxLength = 100;
    public const int CountryLength = 2;
    public const int EmailMaxLength = 254;

    private BuyerIdentity(
        BuyerKind kind,
        Guid? customerId,
        string name,
        string? taxIdentifier,
        string? addressLine1,
        string? addressLine2,
        string? postalCode,
        string? city,
        string? country,
        string? email)
    {
        Kind = kind;
        CustomerId = customerId;
        Name = name;
        TaxIdentifier = taxIdentifier;
        AddressLine1 = addressLine1;
        AddressLine2 = addressLine2;
        PostalCode = postalCode;
        City = city;
        Country = country;
        Email = email;
    }

    public BuyerKind Kind { get; private init; }

    /// <summary>The payer in the Customers module, when the buyer is one.</summary>
    public Guid? CustomerId { get; private init; }

    public string Name { get; private init; }

    /// <summary>Matricule fiscal of a company buyer.</summary>
    public string? TaxIdentifier { get; private init; }

    public string? AddressLine1 { get; private init; }

    public string? AddressLine2 { get; private init; }

    public string? PostalCode { get; private init; }

    public string? City { get; private init; }

    /// <summary>ISO 3166-1 alpha-2 code.</summary>
    public string? Country { get; private init; }

    /// <summary>Where the document is e-mailed (personal data: masked in the audit log).</summary>
    public string? Email { get; private init; }

    /// <summary>The address on one line, or <see langword="null"/> without address.</summary>
    public string? AddressText
    {
        get
        {
            var cityLine = string.Join(' ', new[] { PostalCode, City }.Where(p => !string.IsNullOrEmpty(p)));
            var parts = new[] { AddressLine1, AddressLine2, cityLine, Country }.Where(p => !string.IsNullOrEmpty(p)).ToList();
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
    }

    /// <summary>
    /// A buyer: a name always; a company also its tax identifier and its billing address (line and city). A person's
    /// address is printed when known.
    /// </summary>
    public static Result<BuyerIdentity> Create(
        BuyerKind kind,
        Guid? customerId,
        string? name,
        string? taxIdentifier,
        string? addressLine1,
        string? addressLine2,
        string? postalCode,
        string? city,
        string? country,
        string? email)
    {
        if (!Enum.IsDefined(kind))
        {
            return BillingErrors.BuyerNameRequired;
        }

        var cleanName = SellerIdentity.Clean(name);
        if (cleanName is null)
        {
            return BillingErrors.BuyerNameRequired;
        }

        var tax = SellerIdentity.Clean(taxIdentifier);
        var line1 = SellerIdentity.Clean(addressLine1);
        var line2 = SellerIdentity.Clean(addressLine2);
        var code = SellerIdentity.Clean(postalCode);
        var town = SellerIdentity.Clean(city);
        var countryCode = SellerIdentity.Clean(country)?.ToUpperInvariant();
        var mail = SellerIdentity.Clean(email);
        if (kind == BuyerKind.Company)
        {
            if (tax is null)
            {
                return BillingErrors.BuyerTaxIdentifierRequired;
            }

            if (line1 is null || town is null)
            {
                return BillingErrors.BuyerAddressRequired;
            }
        }

        if (cleanName.Length > NameMaxLength
            || tax?.Length > TaxIdentifierMaxLength
            || line1?.Length > AddressLineMaxLength
            || line2?.Length > AddressLineMaxLength
            || code?.Length > PostalCodeMaxLength
            || town?.Length > CityMaxLength
            || countryCode is { Length: not CountryLength }
            || mail?.Length > EmailMaxLength)
        {
            return BillingErrors.PartyFieldTooLong;
        }

        if (mail is not null && !mail.Contains('@', StringComparison.Ordinal))
        {
            return BillingErrors.EmailInvalid;
        }

        return new BuyerIdentity(kind, customerId, cleanName, tax?.ToUpperInvariant(), line1, line2, code, town, countryCode, mail);
    }
}
