using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Contracts;

/// <summary>
/// Public read API of the Bookings module for other modules and the back office widgets (ids and contract DTOs only).
/// Semantics: docs/conception/phase-1-exploitation.md §2 and §3, ADR-0030.
/// </summary>
/// <remarks>
/// No permission check here (like <c>ICustomersQueries</c>): the caller enforces its own — the back office screens are
/// protected by <c>bookings.read</c> or <c>requests.read</c>, a job runs as the system actor. Person names come from the
/// Customers module (an anonymized person shows an empty name).
/// </remarks>
public interface IBookingsQueries
{
    /// <summary>The rental file, or <see langword="null"/>.</summary>
    Task<BookingSummaryInfo?> GetSummaryAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The rental file of the agency <paramref name="agencyId"/> with a number such as <c>LOC-2027-000123</c> (case and
    /// spaces ignored), or <see langword="null"/>. Numbers are unique within an agency only (ADR-0067): with
    /// <paramref name="agencyId"/> <see langword="null"/>, the file of the agency of the operation — an operation across
    /// agencies then finds nothing.
    /// </summary>
    Task<BookingSummaryInfo?> FindByNumberAsync(Guid? agencyId, string number, CancellationToken cancellationToken = default);

    /// <summary>
    /// The rental files where the person is a driver or the payer (through its individual customer), most recent pick-up
    /// first, drafts excluded.
    /// </summary>
    Task<IReadOnlyList<PersonBookingInfo>> GetBookingsOfPersonAsync(Guid personId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the person is a driver of an open file: a request, an option, a booking or a rental not closed yet (every
    /// status but <c>Draft</c> and the terminal ones). Such a person keeps the images of their documents (retention).
    /// </summary>
    Task<bool> HasOpenBookingsAsync(Guid personId, CancellationToken cancellationToken = default);

    /// <summary>Departures and returns of an agency on a Tunis day (tableau du jour), with the state of the documents.</summary>
    Task<DayAgendaInfo> GetDayAgendaAsync(Guid agencyId, DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>Web requests waiting for an agent (<c>PendingValidation</c>, <c>InformationRequested</c>), the closest hold first.</summary>
    Task<IReadOnlyList<PendingRequestInfo>> GetPendingRequestsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The planning of an agency over <paramref name="range"/>: for each active category its vehicles (rentable windows,
    /// unavailability with the reason, assigned files), the files not assigned yet, the capacity profile of the whole
    /// fleet and the periods where the category has fewer vehicles than files.
    /// </summary>
    Task<PlanningInfo> GetPlanningAsync(Guid agencyId, Period range, CancellationToken cancellationToken = default);

    /// <summary>Rentals in progress whose planned return has passed, the oldest first.</summary>
    Task<IReadOnlyList<LateReturnInfo>> GetLateReturnsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default);

    /// <summary>Files without vehicle whose pick-up is before now + <paramref name="within"/> (overdue pick-ups included), by pick-up.</summary>
    Task<IReadOnlyList<BookingSummaryInfo>> GetUnassignedStartingWithinAsync(
        Guid agencyId,
        TimeSpan within,
        CancellationToken cancellationToken = default);

    /// <summary>Periods of <paramref name="range"/> where a category of the agency has fewer vehicles than files.</summary>
    Task<IReadOnlyList<CapacityShortfallInfo>> GetCapacityShortfallsAsync(
        Guid agencyId,
        Period range,
        CancellationToken cancellationToken = default);
}
