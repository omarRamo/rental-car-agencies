using CarRental.Application.Common.Seeding;

namespace CarRental.Application.Bookings.Seeding;

/// <summary>Demo rental files of the demo agency (development and recette only).</summary>
internal sealed class BookingsDemoSeeder : IDataSeeder
{
    public SeedStage Stage => SeedStage.Demo;

    public int Order => 700;

    public Task SeedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
