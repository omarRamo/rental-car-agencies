using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Contracts;

/// <summary>
/// Read API of the Bookings module for the Reporting module (daily snapshots and indicators, ADR-0045 and ADR-0046): the
/// facts of the rental files of an agency — status, channel and source, planned and actual instants, the totals of the
/// current quote, the vehicles that carried the rental and the status timeline. Lists are read fresh, without tracking;
/// no permission check here (the reporting services check <c>reports.read</c>, the nightly job runs as the system).
/// </summary>
/// <remarks>A separate interface from <see cref="IBookingsQueries"/>, so that its implementers are not affected.</remarks>
public interface IBookingsReportingQueries
{
    /// <summary>
    /// The rental files of the agency (every status but <c>Draft</c>) whose planned rental <c>[PickupAt, ReturnAt)</c>
    /// overlaps the Tunis days <paramref name="from"/> to <paramref name="to"/> (both included), by planned pick-up.
    /// </summary>
    Task<IReadOnlyList<BookingFactsInfo>> GetBookingsOverlappingAsync(
        Guid agencyId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The requests (web, WhatsApp) of the agency first submitted between <paramref name="fromUtc"/> (included) and
    /// <paramref name="toUtc"/> (excluded), whatever their status now, by submission.
    /// </summary>
    Task<IReadOnlyList<BookingFactsInfo>> GetRequestsSubmittedAsync(
        Guid agencyId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The facts of a rental file for the indicators.
/// </summary>
/// <param name="BookingId">The file.</param>
/// <param name="Number">Its readable number (<c>LOC-YYYY-NNNNNN</c>).</param>
/// <param name="AgencyId">Its agency.</param>
/// <param name="Status">Its status now.</param>
/// <param name="Channel">Its sales channel.</param>
/// <param name="Source">The <c>src</c> attribution of the file (deep link, agent), as recorded; <see langword="null"/> when none.</param>
/// <param name="CategoryId">The category sold.</param>
/// <param name="PickupAt">Planned pick-up (UTC), the start of the rented days of the current quote.</param>
/// <param name="ReturnAt">Planned return (UTC).</param>
/// <param name="ActualPickupAt">Departure, once done.</param>
/// <param name="ActualReturnAt">Return, once done.</param>
/// <param name="CreatedAtUtc">Creation of the file.</param>
/// <param name="SubmittedAtUtc">First submission of a request (web, WhatsApp).</param>
/// <param name="Quote">Totals of the current quote version.</param>
/// <param name="CancellationFee">Fee recorded by the cancellation (TTC; zero when free or waived), <see langword="null"/> when not cancelled.</param>
/// <param name="Vehicles">The vehicles that carried the file: the one at the departure (or assigned now when not departed), then one per swap during the rental; empty when no vehicle is assigned.</param>
/// <param name="StatusHistory">Every status change of the timeline, creation included, in order.</param>
public sealed record BookingFactsInfo(
    Guid BookingId,
    string Number,
    Guid AgencyId,
    BookingStatusInfo Status,
    SalesChannel Channel,
    string? Source,
    Guid CategoryId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    DateTimeOffset? ActualPickupAt,
    DateTimeOffset? ActualReturnAt,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? SubmittedAtUtc,
    BookingQuoteFactsInfo Quote,
    Money? CancellationFee,
    IReadOnlyList<BookingVehicleSpanInfo> Vehicles,
    IReadOnlyList<BookingStatusChangeInfo> StatusHistory);

/// <summary>
/// Totals of the current quote version of a file: its priced days, HT, VAT and TTC totals, and the HT of its daily
/// contribution lines (the contribution of 2.000 TND per day is collected for the State: not rental revenue).
/// </summary>
public sealed record BookingQuoteFactsInfo(
    Guid QuoteId,
    int Version,
    int Days,
    Money TotalExclVat,
    Money TotalVat,
    Money TotalInclVat,
    Money DailyContributionExclVat);

/// <summary>The vehicle that carried a file from <paramref name="SinceUtc"/> (<see langword="null"/>: from the start of the rental).</summary>
public sealed record BookingVehicleSpanInfo(Guid VehicleId, DateTimeOffset? SinceUtc);

/// <summary>A status change of the timeline: <paramref name="From"/> is <see langword="null"/> at the creation.</summary>
public sealed record BookingStatusChangeInfo(DateTimeOffset AtUtc, BookingStatusInfo? From, BookingStatusInfo To);
