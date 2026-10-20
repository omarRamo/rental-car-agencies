using CarRental.Domain.Common;

namespace CarRental.Domain.Referential;

/// <summary>
/// A pick-up / return place of an agency: the agency office, an airport (TUN, NBE…) or a delivery point.
/// Codes are short and stable because channel mappings (Booking.com) reference them.
/// </summary>
public sealed class Location : AggregateRoot, ISoftDeletable
{
    public const int CodeMinLength = 2;
    public const int CodeMaxLength = 16;
    public const int NameMaxLength = 120;
    public const int AddressMaxLength = 300;

    private Location()
    {
    }

    private Location(Guid id, Guid agencyId, string code, string name, LocationKind kind, string? address)
        : base(id)
    {
        AgencyId = agencyId;
        Code = code;
        Name = name;
        Kind = kind;
        Address = address;
        IsActive = true;
    }

    public Guid AgencyId { get; private set; }

    /// <summary>Upper-case code, unique among the locations of an agency (e.g. <c>TUN</c>).</summary>
    public string Code { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public LocationKind Kind { get; private set; }

    public string? Address { get; private set; }

    public bool IsActive { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAtUtc { get; private set; }

    public static Result<Location> Create(Guid agencyId, string code, string name, LocationKind kind, string? address = null)
    {
        if (agencyId == Guid.Empty)
        {
            return ReferentialErrors.AgencyRequired;
        }

        var normalizedCode = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalizedCode.Length is < CodeMinLength or > CodeMaxLength
            || !normalizedCode.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-'))
        {
            return ReferentialErrors.LocationCodeInvalid;
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            return ReferentialErrors.LocationNameInvalid;
        }

        if (!Enum.IsDefined(kind))
        {
            return ReferentialErrors.LocationKindInvalid;
        }

        if (address?.Trim().Length > AddressMaxLength)
        {
            return ReferentialErrors.LocationAddressTooLong;
        }

        var location = new Location(
            NewId(),
            agencyId,
            normalizedCode,
            name.Trim(),
            kind,
            string.IsNullOrWhiteSpace(address) ? null : address.Trim());
        location.Raise(new LocationAdded(location.Id, agencyId, normalizedCode));
        return location;
    }

    public Result Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            return ReferentialErrors.LocationNameInvalid;
        }

        Name = name.Trim();
        return Result.Success();
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
