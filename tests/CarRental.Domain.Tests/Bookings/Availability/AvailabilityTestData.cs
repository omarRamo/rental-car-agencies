using System.Globalization;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Bookings.Availability;

/// <summary>Instants as hours from <see cref="T0"/> (high season, UTC) and small builders for availability tests.</summary>
internal static class AvailabilityTestData
{
    public static readonly DateTimeOffset T0 = new(2027, 7, 1, 8, 0, 0, TimeSpan.Zero);

    public static readonly TimeSpan TwoHours = TimeSpan.FromHours(2);

    /// <summary>A window wide enough to mean "always rentable" in a test.</summary>
    public static Period Always => P(-10_000, 10_000);

    public static DateTimeOffset At(double hours) => T0.AddHours(hours);

    public static Period P(double fromHours, double toHours) => Period.Create(At(fromHours), At(toHours)).Value;

    /// <summary>A readable, deterministic id: <c>00000000-0000-0000-0000-00000000000n</c>.</summary>
    public static Guid Id(int number) => Guid.Parse(string.Create(CultureInfo.InvariantCulture, $"00000000-0000-0000-0000-{number:D12}"));

    public static Guid BookingId(int number) => Id(number);

    public static Guid VehicleId(int number) => Id(100_000 + number);

    public static RentableVehicle Vehicle(int number, params Period[] windows) =>
        new(VehicleId(number), string.Create(CultureInfo.InvariantCulture, $"{number:D3} TU {number:D4}"), windows);

    public static RentableVehicle AlwaysRentable(int number) => Vehicle(number, Always);

    public static OccupancyInterval Booking(int number, Period period, int? vehicle = null) =>
        new(BookingId(number), period, vehicle is { } v ? VehicleId(v) : null);
}
