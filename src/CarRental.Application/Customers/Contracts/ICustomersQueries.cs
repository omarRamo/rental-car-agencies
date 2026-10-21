using CarRental.Domain.Common;

namespace CarRental.Application.Customers.Contracts;

/// <summary>
/// Public read API of the Customers module for other modules (ids and contract DTOs only, never document images).
/// Semantics: docs/conception/phase-1-exploitation.md §6, ADR-0026 and ADR-0027.
/// </summary>
/// <remarks>
/// No permission check here: the calling module enforces its own (e.g. <c>bookings.read</c> shows driver names).
/// Document numbers are masked unless the current user holds <c>customers.documents.read</c>. Anonymized persons are
/// left out of searches but still returned by id (bookings and invoices keep referencing them).
/// </remarks>
public interface ICustomersQueries
{
    /// <summary>
    /// Persons matching a name (every word), a phone number (any format, Tunisia by default), a document number (CIN,
    /// passport, licence: start of the number) or an e-mail (start of the address). Each person appears once; ordered
    /// by last then first name; at most <paramref name="limit"/> (1 to 100).
    /// </summary>
    Task<IReadOnlyList<PersonSummaryInfo>> SearchPersonsAsync(string term, int limit = 20, CancellationToken cancellationToken = default);

    /// <summary>The person with its current documents, or <see langword="null"/>.</summary>
    Task<PersonInfo?> GetPersonAsync(Guid personId, CancellationToken cancellationToken = default);

    Task<CustomerInfo?> GetCustomerAsync(Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>The individual payer of a person, if it was already created (see <see cref="ICustomerRegistration.GetOrCreateIndividualCustomerAsync"/>).</summary>
    Task<CustomerInfo?> FindIndividualCustomerAsync(Guid personId, CancellationToken cancellationToken = default);

    /// <summary>Company payers matching a legal name or a tax identifier, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<CustomerInfo>> SearchCompaniesAsync(string term, int limit = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// Internal use only (request validation checklist, silent refusal): never show nor hint it to the applicant
    /// (SPEC §5, "liste noire appliquée sans le signaler au demandeur"). False for an unknown person.
    /// </summary>
    Task<bool> IsBlacklistedAsync(Guid personId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The persons whose phone is exactly <paramref name="phone"/> once normalized (E.164, Tunisia by default), e.g. the
    /// sender of a WhatsApp message; several when relatives share a number (never merge them: ADR-0027). Anonymized persons
    /// are left out; an invalid number finds nobody. Ordered by last then first name, at most 20.
    /// </summary>
    Task<IReadOnlyList<PersonSummaryInfo>> FindPersonsByPhoneAsync(string phone, CancellationToken cancellationToken = default);

    /// <summary>Whether the person currently gives the consent <paramref name="purpose"/> (false for an unknown or anonymized person).</summary>
    Task<bool> HasConsentAsync(Guid personId, ConsentPurposeInfo purpose, CancellationToken cancellationToken = default);

    /// <summary>
    /// Eligibility of the person as a driver for a rental from <paramref name="pickupAt"/> to <paramref name="returnAt"/>,
    /// with the settings of <paramref name="agencyId"/> (platform values when null). Errors: person not found or
    /// anonymized, return not after pick-up.
    /// </summary>
    Task<Result<DriverEligibilityInfo>> CheckDriverEligibilityAsync(
        Guid personId,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        Guid? agencyId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Public funnel (ADR-0050): validates what a visitor typed for a driver and evaluates its eligibility for the rental
    /// on these details only — no stored person is read, so nothing about a person already known can be revealed.
    /// </summary>
    Task<SubmittedDriverCheck> CheckSubmittedDriverAsync(
        SubmittedDriverDetails driver,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        Guid? agencyId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The contact of a driver for a booking: what was typed with the public submission of that booking (the e-mail the
    /// visitor expects to be written to), otherwise the person's own; <see langword="null"/> for an unknown or anonymized person.
    /// </summary>
    Task<BookingContactInfo?> GetBookingContactAsync(Guid personId, Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>The submissions of a person (of one booking when <paramref name="bookingId"/> is given), oldest first.</summary>
    Task<IReadOnlyList<DriverSubmissionInfo>> GetDriverSubmissionsAsync(Guid personId, Guid? bookingId = null, CancellationToken cancellationToken = default);
}
