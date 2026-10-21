using CarRental.Domain.Billing;
using CarRental.Domain.Common;

namespace CarRental.Application.Billing.Fines;

/// <summary>A notice entered by an agent: agency, notice number, authority, instant (UTC) and place, plate, amount, notes.</summary>
public sealed record FineForm(
    Guid AgencyId,
    string? TicketNumber,
    string? Authority,
    DateTimeOffset OffenceAt,
    string? Place,
    string? PlateNumber,
    decimal Amount,
    string? Notes = null);

/// <summary>Filters of the list of fines.</summary>
public sealed record FineListFilter(Guid? AgencyId = null, FineStatus? Status = null, bool OnlyToAssign = false, string? Search = null);

/// <summary>A fine of the list.</summary>
public sealed record FineListItem(
    Guid Id,
    string TicketNumber,
    string Authority,
    DateTimeOffset OffenceAtUtc,
    string PlateNumber,
    Money Amount,
    FineStatus Status,
    FineMatch Match,
    Guid? BookingId,
    string? BookingNumber,
    string? InvoiceNumber);

/// <summary>A driver of the file of a fine, to designate.</summary>
public sealed record FineDriverOption(Guid PersonId, string FullName, bool IsMainDriver);

/// <summary>A rental that had the vehicle at the instant of the offence, to choose from.</summary>
public sealed record FineRentalOption(
    Guid BookingId,
    string Number,
    DateTimeOffset CarriedFromUtc,
    DateTimeOffset? CarriedUntilUtc,
    IReadOnlyList<FineDriverOption> Drivers);

/// <summary>The page of a fine: the notice, the vehicle, the file and the driver, the rentals to choose from, the steps done.</summary>
public sealed record FineDetail(
    Guid Id,
    Guid AgencyId,
    string TicketNumber,
    string Authority,
    DateTimeOffset OffenceAtUtc,
    string Place,
    string PlateNumber,
    Money Amount,
    string? Notes,
    Guid? VehicleId,
    string? VehicleLabel,
    FineMatch Match,
    Guid? BookingId,
    string? BookingNumber,
    Guid? DriverPersonId,
    string? DriverName,
    IReadOnlyList<FineRentalOption> Candidates,
    FineStatus Status,
    DateTimeOffset RecordedAtUtc,
    string RecordedBy,
    DateTimeOffset? DesignatedAtUtc,
    string? DesignatedBy,
    Guid? InvoiceId,
    string? InvoiceNumber,
    DateTimeOffset? PaidAtUtc,
    DateTimeOffset? ClosedAtUtc,
    string? CloseNote,
    FineActions Actions);

/// <summary>What the current user may do on a fine page.</summary>
public sealed record FineActions(bool CanAssign, bool CanProduceDesignation, bool CanMarkDesignated, bool CanReinvoice, bool CanMarkPaid, bool CanClose, Money ManagementFee);

/// <summary>The designation form produced for the authority (PDF, never stored).</summary>
public sealed record FineDesignationFile(byte[] Content, string FileName);
