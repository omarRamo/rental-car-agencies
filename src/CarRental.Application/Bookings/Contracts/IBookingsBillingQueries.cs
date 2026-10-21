using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Contracts;

/// <summary>
/// Read API of the Bookings module for the Billing module (package 3A): what an invoice needs from a rental file — its
/// parties, its actual period, its cancellation fee and the lines of its current quote — the rentals that had a vehicle at
/// an instant (traffic fines) and the files to invoice. Read fresh, without tracking; no permission check here (the
/// Billing services check <c>invoices.*</c>, <c>fines.manage</c>; the outbox runs as the system).
/// </summary>
/// <remarks>A separate interface from <see cref="IBookingsQueries"/>, so that its implementers are not affected.</remarks>
public interface IBookingsBillingQueries
{
    /// <summary>The billing facts of a file and the lines of its current quote version, or <see langword="null"/>.</summary>
    Task<BookingBillingInfo?> GetBillingFactsAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The rentals (in progress, returned or closed) during which <paramref name="vehicleId"/> carried the file at
    /// <paramref name="instant"/>: actual pick-up (or the swap that brought the vehicle) at or before the instant, actual
    /// return (or the swap that took it away) after it — open while the rental goes on.
    /// </summary>
    Task<IReadOnlyList<VehicleRentalInfo>> FindRentalsOfVehicleAtAsync(Guid vehicleId, DateTimeOffset instant, CancellationToken cancellationToken = default);

    /// <summary>
    /// The spans during which <paramref name="vehicleId"/> carried a rental (in progress, returned or closed) overlapping
    /// <c>[fromUtc, toUtc)</c>, by start: what an agent chooses from when a fine has no rental or several.
    /// </summary>
    Task<IReadOnlyList<VehicleRentalInfo>> GetRentalsOfVehicleAsync(
        Guid vehicleId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The files of an agency that call for an invoice — closed files, and cancelled files with a cancellation fee —
    /// whose status changed since <paramref name="sinceUtc"/>, the most recent first (at most <paramref name="limit"/>).
    /// </summary>
    Task<IReadOnlyList<BookingToInvoiceInfo>> GetFilesToInvoiceAsync(
        Guid agencyId,
        DateTimeOffset sinceUtc,
        int limit = 200,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Kind of a line of a frozen quote (same values as the domain enum): the pricing engine kinds plus the lines of the
/// Bookings module (extension, manual discount).
/// </summary>
public enum BookingQuoteLineKindInfo
{
    Rental = 1,
    DurationDiscount = 2,
    Promotion = 3,
    GuaranteedModel = 4,
    Extra = 5,
    FranchiseWaiver = 6,
    YoungDriver = 7,
    Delivery = 8,
    AfterHours = 9,
    OneWay = 10,
    DailyContribution = 11,
    Extension = 20,
    ManualDiscount = 21,
}

/// <summary>
/// What an invoice needs from a rental file: its identity, channel and channel reference, planned and actual instants,
/// parties (Customers ids), the vehicle, the cancellation fee recorded (TTC; zero when free or waived) and its current
/// quote version.
/// </summary>
public sealed record BookingBillingInfo(
    Guid BookingId,
    string Number,
    Guid AgencyId,
    BookingStatusInfo Status,
    SalesChannel Channel,
    string? ChannelReference,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    DateTimeOffset? ActualPickupAt,
    DateTimeOffset? ActualReturnAt,
    Guid? MainDriverId,
    Guid? SecondaryDriverId,
    Guid? PayerCustomerId,
    Guid? VehicleId,
    Money? CancellationFee,
    DateTimeOffset StatusChangedAtUtc,
    BookingQuoteBillingInfo Quote);

/// <summary>The current quote version of a file: days, totals, deposit (the franchise of the damages) and its lines in order.</summary>
public sealed record BookingQuoteBillingInfo(
    Guid QuoteId,
    int Version,
    int Days,
    Money TotalExclVat,
    Money TotalVat,
    Money TotalInclVat,
    Money Deposit,
    bool IncludesFranchiseWaiver,
    IReadOnlyList<BookingQuoteLineInfo> Lines);

/// <summary>
/// A frozen quote line: kind, code (the extra code for extras), label as a resource key with invariant arguments, quantity,
/// unit price VAT included, HT, VAT, TTC and rate. The daily contribution is entered excluding VAT.
/// </summary>
public sealed record BookingQuoteLineInfo(
    int Position,
    BookingQuoteLineKindInfo Kind,
    string Code,
    string LabelKey,
    IReadOnlyList<string> LabelArguments,
    int Quantity,
    Money UnitPrice,
    Money ExclVat,
    Money Vat,
    Money InclVat,
    decimal VatRate);

/// <summary>
/// A span during which a vehicle carried a rental file: from the actual pick-up (or the swap that brought it) to the
/// actual return (or the swap that took it away); <see cref="CarriedUntilUtc"/> is <see langword="null"/> while it goes on.
/// </summary>
public sealed record VehicleRentalInfo(
    Guid BookingId,
    string Number,
    Guid AgencyId,
    BookingStatusInfo Status,
    Guid VehicleId,
    DateTimeOffset CarriedFromUtc,
    DateTimeOffset? CarriedUntilUtc,
    Guid? MainDriverId,
    Guid? SecondaryDriverId,
    Guid? PayerCustomerId);

/// <summary>A file that calls for an invoice: closed, or cancelled with a cancellation fee (TTC).</summary>
public sealed record BookingToInvoiceInfo(
    Guid BookingId,
    string Number,
    BookingStatusInfo Status,
    DateTimeOffset StatusChangedAtUtc,
    Guid? PayerCustomerId,
    Guid? MainDriverId,
    Money TotalInclVat,
    Money? CancellationFee);
