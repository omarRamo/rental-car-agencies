namespace CarRental.Application.Referential.Contracts;

/// <summary>Public read API of the Referential module (agencies and pick-up/return locations).</summary>
public interface IReferentialQueries
{
    Task<IReadOnlyList<AgencyInfo>> GetAgenciesAsync(CancellationToken cancellationToken = default);

    /// <summary>Active locations, optionally of one agency, ordered by kind then code.</summary>
    Task<IReadOnlyList<LocationInfo>> GetLocationsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default);

    Task<LocationInfo?> FindLocationByCodeAsync(string code, CancellationToken cancellationToken = default);
}

/// <summary>
/// An agency; <see cref="Latitude"/> and <see cref="Longitude"/> (WGS 84) are both set or both null; <see cref="Legal"/>
/// is the legal identity printed on its invoices (the Billing module refuses to invoice while it is incomplete).
/// </summary>
public sealed record AgencyInfo(
    Guid Id,
    string Name,
    string City,
    string? Phone,
    string? Email,
    bool IsActive,
    string? Address = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    AgencyLegalIdentityInfo? Legal = null);

/// <summary>
/// The legal identity of an agency (the seller of its invoices): legal name, tax identifier (matricule fiscal), trade
/// register number, legal address (head office; the office address applies when empty) and the footer of the invoices.
/// </summary>
public sealed record AgencyLegalIdentityInfo(
    string? LegalName,
    string? TaxIdentifier,
    string? TradeRegisterNumber,
    string? LegalAddress,
    string? InvoiceFooter);

/// <summary>A pick-up / return location. <see cref="Kind"/> uses the contract enum, not the domain one.</summary>
public sealed record LocationInfo(Guid Id, Guid AgencyId, string Code, string Name, LocationKindInfo Kind, string? Address);

/// <summary>Kind of location as exposed to other modules (same values as the domain enum).</summary>
public enum LocationKindInfo
{
    AgencyOffice = 1,
    Airport = 2,
    DeliveryPoint = 3,
}
