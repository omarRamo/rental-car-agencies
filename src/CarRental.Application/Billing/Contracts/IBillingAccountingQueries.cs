using CarRental.Domain.Common;

namespace CarRental.Application.Billing.Contracts;

/// <summary>
/// Read API of the Billing module for the expenses and the monthly closing (package 3B): the claims an expense may pay, and
/// the files of a month that call for an invoice not issued yet. No permission check (the caller checks its own).
/// </summary>
/// <remarks>A separate interface from <see cref="IBillingQueries"/>, so that its implementers are not affected.</remarks>
public interface IBillingAccountingQueries
{
    /// <summary>The claims of an agency, of one vehicle when <paramref name="vehicleId"/> is given, the latest accident first.</summary>
    Task<IReadOnlyList<ClaimSummaryInfo>> GetClaimsAsync(Guid agencyId, Guid? vehicleId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The files of an agency closed, or cancelled with a fee, on the Tunis days <paramref name="from"/> to
    /// <paramref name="to"/> (both included) whose invoice (final invoice, or invoice of the fee) is not issued.
    /// </summary>
    Task<IReadOnlyList<PendingInvoicingInfo>> GetFilesToInvoiceAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

/// <summary>Status of a claim (same values as the domain enum).</summary>
public enum ClaimStatusInfo
{
    Open = 1,
    AtInsurer = 2,
    Closed = 3,
}

/// <summary>A claim of a vehicle: accident instant, file number, insurer and its claim number, status.</summary>
public sealed record ClaimSummaryInfo(
    Guid Id,
    Guid AgencyId,
    Guid VehicleId,
    string? BookingNumber,
    DateTimeOffset AccidentAtUtc,
    string? Insurer,
    string? InsurerClaimNumber,
    ClaimStatusInfo Status);

/// <summary>A file of the month whose invoice is not issued: what it calls for and when its status changed.</summary>
public sealed record PendingInvoicingInfo(Guid BookingId, string BookingNumber, InvoiceKindInfo Kind, DateTimeOffset StatusChangedAtUtc, Money Amount);
