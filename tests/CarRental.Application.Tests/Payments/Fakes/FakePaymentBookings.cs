using CarRental.Application.Bookings.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Payments.Fakes;

/// <summary>
/// The rental files as the Payments module sees them (<see cref="IBookingsQueries.GetSummaryAsync"/>): any booking
/// belongs to the agency of the harness, unless a test places it in another agency (<see cref="PlaceIn"/>) or makes it
/// unknown to the operation (<see cref="Hide"/>: a file of another agency hidden by the agency filter, or no file).
/// </summary>
internal sealed class FakePaymentBookings(Guid harnessAgencyId) : IBookingsQueries
{
    private readonly Dictionary<Guid, Guid?> _agencies = [];

    /// <summary>The booking belongs to <paramref name="otherAgencyId"/> (the operation sees it: the support, the system).</summary>
    public void PlaceIn(Guid bookingId, Guid otherAgencyId) => _agencies[bookingId] = otherAgencyId;

    /// <summary>The operation does not see the booking.</summary>
    public void Hide(Guid bookingId) => _agencies[bookingId] = null;

    public Task<BookingSummaryInfo?> GetSummaryAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var agency = _agencies.TryGetValue(bookingId, out var placed) ? placed : harnessAgencyId;
        return Task.FromResult(agency is { } owner ? Summary(bookingId, owner) : null);
    }

    public Task<BookingSummaryInfo?> FindByNumberAsync(Guid? agencyId, string number, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<PersonBookingInfo>> GetBookingsOfPersonAsync(Guid personId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> HasOpenBookingsAsync(Guid personId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<DayAgendaInfo> GetDayAgendaAsync(Guid agencyId, DateOnly date, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<PendingRequestInfo>> GetPendingRequestsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<PlanningInfo> GetPlanningAsync(Guid agencyId, Period range, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<LateReturnInfo>> GetLateReturnsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<BookingSummaryInfo>> GetUnassignedStartingWithinAsync(Guid agencyId, TimeSpan within, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<CapacityShortfallInfo>> GetCapacityShortfallsAsync(Guid agencyId, Period range, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    private static BookingSummaryInfo Summary(Guid bookingId, Guid agencyId)
    {
        var pickup = new DateTimeOffset(2027, 7, 1, 9, 0, 0, TimeSpan.Zero);
        return new BookingSummaryInfo(
            bookingId,
            "LOC-2027-000001",
            agencyId,
            BookingStatusInfo.Confirmed,
            SalesChannel.Counter,
            ChannelReference: null,
            CategoryId: Guid.CreateVersion7(),
            PickupLocationId: Guid.CreateVersion7(),
            ReturnLocationId: Guid.CreateVersion7(),
            pickup,
            pickup.AddDays(3),
            ActualPickupAt: null,
            ActualReturnAt: null,
            AssignedVehicleId: null,
            GuaranteedVehicleId: null,
            MainDriverId: null,
            SecondaryDriverId: null,
            PayerCustomerId: null,
            CurrentQuoteId: Guid.CreateVersion7(),
            CurrentQuoteVersion: 1,
            Money.Tnd(300m),
            Money.Tnd(500m),
            CancellationFee: null,
            HoldExpiresAt: null,
            OptionExpiresAt: null,
            PaymentDueAt: null,
            pickup.AddDays(-10),
            pickup.AddDays(-10));
    }
}
