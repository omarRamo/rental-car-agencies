using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Channels.Seeding;

/// <summary>
/// Demo channel (development and recette only): the Booking.com channel of the demo agency, sold by hand on the
/// extranet (state manual, manual adapter, paid on site, 15 % commission), each active category mapped to its ACRISS
/// code and each location to its own code. The codes are the agency's demo choices, not a Booking.com list.
/// Idempotent: nothing is added once the channel exists.
/// </summary>
internal sealed partial class ChannelsDemoSeeder(
    IAppDbContext db,
    IReferentialQueries referential,
    IFleetQueries fleet,
    TimeProvider timeProvider,
    ILogger<ChannelsDemoSeeder> logger) : IDataSeeder
{
    public const string DemoAgencyName = "Agence Démo Tunis";

    public const string DemoChannelCode = "booking-com";

    public SeedStage Stage => SeedStage.Demo;

    public int Order => 750;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (await db.Set<Channel>().AnyAsync(c => c.Code == DemoChannelCode, cancellationToken))
        {
            return;
        }

        var agency = (await referential.GetAgenciesAsync(cancellationToken)).FirstOrDefault(a => a.Name == DemoAgencyName);
        if (agency is null)
        {
            LogMissingDemoAgency();
            return;
        }

        var stamp = new ChannelStamp(timeProvider.GetUtcNow(), "system:seed");
        var channel = Ensure(Channel.Create(agency.Id, DemoChannelCode, "Booking.com", SalesChannel.BookingCom, ChannelAdapterKind.Manual, ChannelPaymentMode.PayOnSite, 0.15m, stamp));
        Ensure(channel.ChangeState(ChannelState.Manual, stamp));
        foreach (var category in await fleet.GetCategoriesAsync(agency.Id, includeInactive: false, cancellationToken))
        {
            Ensure(channel.MapCategory(category.Id, category.AcrissCode, null, stamp));
        }

        foreach (var location in await referential.GetLocationsAsync(agency.Id, cancellationToken))
        {
            Ensure(channel.MapLocation(location.Id, location.Code, stamp));
        }

        db.Set<Channel>().Add(channel);
        await db.SaveChangesAsync(cancellationToken);
        LogSeeded(channel.Mappings.Count);
    }

    private static T Ensure<T>(Result<T> result) =>
        result.IsSuccess ? result.Value : throw new InvalidOperationException($"Demo channel data could not be built: {result.Error}.");

    private static void Ensure(Result result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Demo channel data could not be built: {result.Error}.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo agency not found: no demo channel.")]
    private partial void LogMissingDemoAgency();

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo Booking.com channel created with {Mappings} mapping(s).")]
    private partial void LogSeeded(int mappings);
}
