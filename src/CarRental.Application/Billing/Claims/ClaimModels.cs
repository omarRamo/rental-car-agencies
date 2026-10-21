using CarRental.Domain.Billing;
using CarRental.Domain.Common;

namespace CarRental.Application.Billing.Claims;

/// <summary>A claim to open: agency, vehicle, the file during which it happened (its number, optional) and the details.</summary>
public sealed record ClaimForm(
    Guid AgencyId,
    Guid VehicleId,
    string? BookingNumber,
    DateTimeOffset AccidentAt,
    string? Place,
    string? Description,
    string? Insurer,
    string? InsurerClaimNumber,
    decimal Franchise);

/// <summary>Filters of the list of claims.</summary>
public sealed record ClaimListFilter(Guid? AgencyId = null, ClaimStatus? Status = null, Guid? VehicleId = null);

/// <summary>A claim of the list.</summary>
public sealed record ClaimListItem(
    Guid Id,
    DateTimeOffset AccidentAtUtc,
    Guid VehicleId,
    string VehicleLabel,
    string? BookingNumber,
    string? Insurer,
    string? InsurerClaimNumber,
    ClaimStatus Status,
    Money TotalCosts,
    Money TotalReimbursements,
    Money NetCost);

/// <summary>A cost or a reimbursement of a claim.</summary>
public sealed record ClaimEntryView(Guid Id, DateOnly Date, string Label, Money Amount, string? Reference, DateTimeOffset RecordedAtUtc, string RecordedBy);

/// <summary>A file of a claim (served by the back office only).</summary>
public sealed record ClaimDocumentView(Guid Id, ClaimDocumentKind Kind, string FileName, string ContentType, long Length, DateTimeOffset UploadedAtUtc, string UploadedBy);

/// <summary>The page of a claim.</summary>
public sealed record ClaimDetail(
    Guid Id,
    Guid AgencyId,
    Guid VehicleId,
    string VehicleLabel,
    Guid? BookingId,
    string? BookingNumber,
    DateTimeOffset AccidentAtUtc,
    string? Place,
    string Description,
    string? Insurer,
    string? InsurerClaimNumber,
    Money Franchise,
    ClaimStatus Status,
    DateTimeOffset OpenedAtUtc,
    string OpenedBy,
    DateTimeOffset? ClosedAtUtc,
    IReadOnlyList<ClaimEntryView> Costs,
    IReadOnlyList<ClaimEntryView> Reimbursements,
    IReadOnlyList<ClaimDocumentView> Documents,
    Money TotalCosts,
    Money TotalReimbursements,
    Money NetCost);

/// <summary>A file of a claim to stream: container, path, content type, entity tag and the name offered.</summary>
public sealed record ClaimFile(string Container, string Path, string ContentType, string ETag, string FileName);
