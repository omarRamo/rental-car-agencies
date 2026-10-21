using System.Linq.Expressions;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Security;
using CarRental.Domain.Bookings;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings;

/// <summary>Mapping between the domain of the Bookings module and its contracts.</summary>
internal static class BookingMapping
{
    /// <summary>Projection of a rental file to its contract summary (translated to SQL).</summary>
    public static readonly Expression<Func<Booking, BookingSummaryInfo>> ToSummary = b => new BookingSummaryInfo(
        b.Id,
        b.Number,
        b.AgencyId,
        (BookingStatusInfo)b.Status,
        b.Channel,
        b.ChannelReference,
        b.CategoryId,
        b.PickupLocationId,
        b.ReturnLocationId,
        b.PickupAt,
        b.ReturnAt,
        b.ActualPickupAt,
        b.ActualReturnAt,
        b.AssignedVehicleId,
        b.GuaranteedVehicleId,
        b.MainDriverId,
        b.SecondaryDriverId,
        b.PayerCustomerId,
        b.CurrentQuoteId,
        b.CurrentQuoteVersion,
        b.TotalInclVat,
        b.Deposit,
        b.CancellationFee,
        b.HoldExpiresAt,
        b.OptionExpiresAt,
        b.PaymentDueAt,
        b.CreatedAtUtc,
        b.StatusChangedAtUtc);

    private static readonly Func<Booking, BookingSummaryInfo> CompiledSummary = ToSummary.Compile();

    public static BookingSummaryInfo Summary(this Booking booking) => CompiledSummary(booking);

    public static BookingStatusInfo ToInfo(this BookingStatus status) => (BookingStatusInfo)status;

    public static BookingStatus ToDomain(this BookingStatusInfo status) => (BookingStatus)status;

    /// <summary>The message published with a status change (creation included).</summary>
    public static BookingStatusChanged StatusChanged(this Booking booking, BookingStatus? previousStatus, DateTimeOffset at) => new(
        booking.Id,
        booking.AgencyId,
        booking.Number,
        booking.Channel,
        previousStatus?.ToInfo(),
        booking.Status.ToInfo(),
        booking.TotalInclVat,
        booking.PaymentDueAt,
        at);

    /// <summary>The actor of a command from the user context of the operation.</summary>
    public static BookingActor ActorFor(IUserContext user, SalesChannel? channel = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new BookingActor(user.IsSystem ? null : user.UserId, user.UserName ?? BookingActor.SystemName, channel);
    }
}
