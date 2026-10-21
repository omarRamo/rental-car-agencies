using CarRental.Domain.Channels;
using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Channels;

internal static class ChannelTestData
{
    public static readonly Guid AgencyId = Guid.CreateVersion7();

    /// <summary>1 June 2027, 09:00 in Tunis.</summary>
    public static readonly DateTimeOffset Now = TunisTime.FromLocal(new DateTime(2027, 6, 1, 9, 0, 0, DateTimeKind.Unspecified));

    public static ChannelStamp Stamp { get; } = new(Now, "gerant@demo.local");

    public static DateOnly Today => TunisTime.DateOf(Now);

    public static Channel NewChannel(string code = "booking-com", decimal rate = 0.15m) =>
        Channel.Create(AgencyId, code, "Booking.com", SalesChannel.BookingCom, ChannelAdapterKind.Manual, ChannelPaymentMode.PayOnSite, rate, Stamp).Value;

    public static Money Tnd(decimal amount) => Money.Tnd(amount);
}
