using CarRental.Domain.Common;

namespace CarRental.Domain.Referential;

/// <summary>
/// A rental agency: legal entity, main office and tenant of the platform (ADR-0067). The support registers it, gives it
/// the address of its public site (<see cref="Slug"/>) and its optional <see cref="Modules"/>; its staff then manage it.
/// </summary>
public sealed class Agency : AggregateRoot, ISoftDeletable
{
    public const int NameMaxLength = 120;
    public const int LegalNameMaxLength = 200;
    public const int TaxIdentifierMaxLength = 32;
    public const int CityMaxLength = 80;
    public const int AddressMaxLength = 300;
    public const int PhoneMaxLength = 32;
    public const int EmailMaxLength = 254;
    public const int TradeRegisterNumberMaxLength = 64;
    public const int LegalAddressMaxLength = 300;
    public const int InvoiceFooterMaxLength = 1000;

    /// <summary>Decimals kept for the coordinates (about 10 cm).</summary>
    public const int CoordinateDecimals = 6;

    private Agency()
    {
    }

    private Agency(Guid id, AgencyDetails details, string slug, AgencyModules modules)
        : base(id)
    {
        Apply(details);
        Slug = slug;
        Modules = modules;
        IsActive = true;
    }

    public string Name { get; private set; } = null!;

    /// <summary>Address of the public site (<c>/{slug}/{culture}</c>), unique on the platform (see <see cref="AgencySlug"/>).</summary>
    public string Slug { get; private set; } = null!;

    /// <summary>The optional modules switched on by the support (see <see cref="AgencyModules"/>).</summary>
    public AgencyModules Modules { get; private set; }

    public string? LegalName { get; private set; }

    /// <summary>Tunisian tax identifier (matricule fiscal), mandatory on invoices.</summary>
    public string? TaxIdentifier { get; private set; }

    public string City { get; private set; } = null!;

    public string? Address { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    /// <summary>WGS 84 latitude of the office (map link and structured data of the public site), with <see cref="Longitude"/>.</summary>
    public decimal? Latitude { get; private set; }

    /// <summary>WGS 84 longitude of the office, set together with <see cref="Latitude"/>.</summary>
    public decimal? Longitude { get; private set; }

    /// <summary>Trade register number (registre national des entreprises), printed on invoices.</summary>
    public string? TradeRegisterNumber { get; private set; }

    /// <summary>Legal address (head office) printed on invoices; the office address when not set.</summary>
    public string? LegalAddress { get; private set; }

    /// <summary>Free text printed at the bottom of invoices and credit notes (capital, bank details…).</summary>
    public string? InvoiceFooter { get; private set; }

    /// <summary>
    /// False while the agency is suspended by the support: its public site is offline and its staff cannot sign in. Its
    /// data and background work are kept.
    /// </summary>
    public bool IsActive { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAtUtc { get; private set; }

    /// <summary>
    /// Registers an agency, active. <paramref name="slug"/> defaults to the one proposed from the name
    /// (<see cref="AgencySlug.FromName"/>) and <paramref name="modules"/> to <see cref="AgencyModuleCatalog.Defaults"/>; the
    /// caller checks that the slug is free.
    /// </summary>
    public static Result<Agency> Register(AgencyDetails details, string? slug = null, AgencyModules? modules = null)
    {
        ArgumentNullException.ThrowIfNull(details);
        var normalized = details.Normalize();
        var validation = Validate(normalized);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var normalizedSlug = AgencySlug.Normalize(slug ?? AgencySlug.FromName(normalized.Name));
        if (normalizedSlug.IsFailure)
        {
            return normalizedSlug.Error;
        }

        var enabled = modules ?? AgencyModuleCatalog.Defaults;
        if (!AgencyModuleCatalog.AreDefined(enabled))
        {
            return ReferentialErrors.AgencyModulesInvalid;
        }

        var agency = new Agency(NewId(), normalized, normalizedSlug.Value, enabled);
        agency.Raise(new AgencyRegistered(agency.Id, agency.Name, agency.Slug));
        return agency;
    }

    public Result UpdateDetails(AgencyDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var normalized = details.Normalize();
        var validation = Validate(normalized);
        if (validation.IsFailure)
        {
            return validation;
        }

        Apply(normalized);
        Raise(new AgencyDetailsUpdated(Id));
        return Result.Success();
    }

    /// <summary>
    /// The legal identity printed on the invoices (seller): legal name, tax identifier (matricule fiscal), trade register
    /// number, legal address and footer. Blank fields are cleared; an incomplete identity is allowed here, the Billing
    /// module refuses to issue an invoice until legal name, tax identifier and an address are known.
    /// </summary>
    public Result UpdateLegalIdentity(AgencyLegalIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var legalName = AgencyDetails.NullIfBlank(identity.LegalName);
        var taxIdentifier = AgencyDetails.NullIfBlank(identity.TaxIdentifier)?.ToUpperInvariant();
        var register = AgencyDetails.NullIfBlank(identity.TradeRegisterNumber);
        var address = AgencyDetails.NullIfBlank(identity.LegalAddress);
        var footer = AgencyDetails.NullIfBlank(identity.InvoiceFooter);
        if (legalName?.Length > LegalNameMaxLength
            || taxIdentifier?.Length > TaxIdentifierMaxLength
            || register?.Length > TradeRegisterNumberMaxLength
            || address?.Length > LegalAddressMaxLength
            || footer?.Length > InvoiceFooterMaxLength)
        {
            return ReferentialErrors.AgencyFieldTooLong;
        }

        LegalName = legalName;
        TaxIdentifier = taxIdentifier;
        TradeRegisterNumber = register;
        LegalAddress = address;
        InvoiceFooter = footer;
        Raise(new AgencyLegalIdentityUpdated(Id));
        return Result.Success();
    }

    /// <summary>Changes the address of the public site; the caller checks that the new slug is free.</summary>
    public Result ChangeSlug(string slug)
    {
        var normalized = AgencySlug.Normalize(slug);
        if (normalized.IsFailure)
        {
            return normalized.Error;
        }

        if (string.Equals(normalized.Value, Slug, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        var previous = Slug;
        Slug = normalized.Value;
        Raise(new AgencySlugChanged(Id, previous, Slug));
        return Result.Success();
    }

    /// <summary>Switches the optional modules: exactly <paramref name="modules"/> are on afterwards.</summary>
    public Result SetModules(AgencyModules modules)
    {
        if (!AgencyModuleCatalog.AreDefined(modules))
        {
            return ReferentialErrors.AgencyModulesInvalid;
        }

        if (modules == Modules)
        {
            return Result.Success();
        }

        var previous = Modules;
        Modules = modules;
        Raise(new AgencyModulesChanged(Id, previous, modules));
        return Result.Success();
    }

    /// <summary>True when every module of <paramref name="module"/> is on.</summary>
    public bool HasModule(AgencyModules module) => (Modules & module) == module;

    /// <summary>Suspends the agency (public site offline, staff sign-in refused).</summary>
    public void Deactivate()
    {
        if (IsActive)
        {
            IsActive = false;
            Raise(new AgencyActivationChanged(Id, IsActive: false));
        }
    }

    /// <summary>Lifts the suspension.</summary>
    public void Activate()
    {
        if (!IsActive)
        {
            IsActive = true;
            Raise(new AgencyActivationChanged(Id, IsActive: true));
        }
    }

    /// <summary>Validates normalized details (trimmed, blank optional fields set to null).</summary>
    private static Result Validate(AgencyDetails details)
    {
        if (details.Name.Length is 0 or > NameMaxLength)
        {
            return ReferentialErrors.AgencyNameInvalid;
        }

        if (details.City.Length is 0 or > CityMaxLength)
        {
            return ReferentialErrors.AgencyCityInvalid;
        }

        if (details.LegalName?.Length > LegalNameMaxLength
            || details.TaxIdentifier?.Length > TaxIdentifierMaxLength
            || details.Address?.Length > AddressMaxLength
            || details.Phone?.Length > PhoneMaxLength)
        {
            return ReferentialErrors.AgencyFieldTooLong;
        }

        if (details.Email is { } email && (email.Length > EmailMaxLength || !email.Contains('@', StringComparison.Ordinal)))
        {
            return ReferentialErrors.AgencyEmailInvalid;
        }

        if (details.Latitude.HasValue != details.Longitude.HasValue
            || details.Latitude is < -90m or > 90m
            || details.Longitude is < -180m or > 180m)
        {
            return ReferentialErrors.AgencyCoordinatesInvalid;
        }

        return Result.Success();
    }

    private void Apply(AgencyDetails details)
    {
        Name = details.Name;
        City = details.City;
        LegalName = details.LegalName;
        TaxIdentifier = details.TaxIdentifier;
        Address = details.Address;
        Phone = details.Phone;
        Email = details.Email;
        Latitude = details.Latitude;
        Longitude = details.Longitude;
    }
}

/// <summary>Editable details of an agency; the coordinates (WGS 84) of the office go together or not at all.</summary>
public sealed record AgencyDetails(
    string Name,
    string City,
    string? LegalName = null,
    string? TaxIdentifier = null,
    string? Address = null,
    string? Phone = null,
    string? Email = null,
    decimal? Latitude = null,
    decimal? Longitude = null)
{
    internal AgencyDetails Normalize() => new(
        Name?.Trim() ?? string.Empty,
        City?.Trim() ?? string.Empty,
        NullIfBlank(LegalName),
        NullIfBlank(TaxIdentifier),
        NullIfBlank(Address),
        NullIfBlank(Phone),
        NullIfBlank(Email),
        RoundCoordinate(Latitude),
        RoundCoordinate(Longitude));

    private static decimal? RoundCoordinate(decimal? value) =>
        value is { } coordinate ? Math.Round(coordinate, Agency.CoordinateDecimals, MidpointRounding.AwayFromZero) : null;

    internal static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The legal identity of an agency, printed as the seller on its invoices: legal name, tax identifier (matricule fiscal),
/// trade register number, legal address (head office) and the footer of the invoices.
/// </summary>
public sealed record AgencyLegalIdentity(
    string? LegalName,
    string? TaxIdentifier,
    string? TradeRegisterNumber,
    string? LegalAddress,
    string? InvoiceFooter);
