using CarRental.Domain.Common;

namespace CarRental.Domain.Referential;

/// <summary>
/// A rental agency (legal entity and main office). One agency at launch; the model is ready for several.
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

    private Agency()
    {
    }

    private Agency(Guid id, AgencyDetails details)
        : base(id)
    {
        Apply(details);
        IsActive = true;
    }

    public string Name { get; private set; } = null!;

    public string? LegalName { get; private set; }

    /// <summary>Tunisian tax identifier (matricule fiscal), mandatory on invoices.</summary>
    public string? TaxIdentifier { get; private set; }

    public string City { get; private set; } = null!;

    public string? Address { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public bool IsActive { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAtUtc { get; private set; }

    public static Result<Agency> Register(AgencyDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var normalized = details.Normalize();
        var validation = Validate(normalized);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var agency = new Agency(NewId(), normalized);
        agency.Raise(new AgencyRegistered(agency.Id, agency.Name));
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

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;

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
    }
}

/// <summary>Editable details of an agency.</summary>
public sealed record AgencyDetails(
    string Name,
    string City,
    string? LegalName = null,
    string? TaxIdentifier = null,
    string? Address = null,
    string? Phone = null,
    string? Email = null)
{
    internal AgencyDetails Normalize() => new(
        Name?.Trim() ?? string.Empty,
        City?.Trim() ?? string.Empty,
        NullIfBlank(LegalName),
        NullIfBlank(TaxIdentifier),
        NullIfBlank(Address),
        NullIfBlank(Phone),
        NullIfBlank(Email));

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
