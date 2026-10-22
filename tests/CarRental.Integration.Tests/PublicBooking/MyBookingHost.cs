using CarRental.Application.Common.Persistence;
using CarRental.Domain.Referential;
using CarRental.Integration.Tests.Bookings;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.PublicBooking;

/// <summary>
/// The host of "Ma réservation" over HTTP (ADR-0051): the public site finds the bookings of its own agency only
/// (ADR-0067), and the site of the shared host is the main demo agency, whose settings the other tests rely on. This
/// host has a database of its own without the demo data: the agency of its site is <see cref="Agency"/>, the first agency
/// created, at start-up. A scenario changes a setting of that agency with <see cref="SetAsync"/>, which takes it back at
/// the end: the tests of the collection run one after the other.
/// </summary>
/// <remarks>
/// A database of its own, like the other dedicated hosts: on the shared one, its outbox pump and job server would process
/// the messages of the other tests with its own handlers and e-mail folder.
/// </remarks>
public sealed class MyBookingHost : IAsyncLifetime
{
    public const string Database = "CarRental_MyBooking";

    public MyBookingHost(IntegrationHost shared)
    {
        ArgumentNullException.ThrowIfNull(shared);
        Factory = new CarRentalAppFactory(shared.Containers, Database)
        {
            ExtraSettings = new Dictionary<string, string?>
            {
                ["Seed:Demo"] = "false",
            },
        };
        Host = IntegrationHost.Over(shared.Containers, Factory);
    }

    public CarRentalAppFactory Factory { get; }

    /// <summary>The scopes and clients of this host, for the worlds written for the shared one.</summary>
    public IntegrationHost Host { get; }

    /// <summary>The agency of the public site of this host: an office, an airport, ECMR and CDMR vehicles, a published plan.</summary>
    internal BookingAgency Agency { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        // Starts the host (migrations and reference data), then creates the agency of its site.
        _ = Factory.Server;
        Agency = await BookingWorld.CreateAsync(Host, CancellationToken.None, ecmrVehicles: 3);
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();

    /// <summary>Stores a value of a setting for the agency of the site; disposing the result takes it back.</summary>
    internal async Task<IAsyncDisposable> SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        await BookingWorld.SetAsync(Host, Agency.AgencyId, key, value, cancellationToken);
        return new SettingOverride(this, key);
    }

    private async Task RemoveAsync(string key)
    {
        await using var scope = Host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var setting = await db.Set<Setting>().SingleAsync(s => s.Key == key && s.AgencyId == Agency.AgencyId);
        setting.Remove().IsSuccess.ShouldBeTrue();
        db.Set<Setting>().Remove(setting);
        await db.SaveChangesAsync();
    }

    private sealed class SettingOverride(MyBookingHost host, string key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await host.RemoveAsync(key);
    }
}

/// <summary>The classes that play on the <see cref="MyBookingHost"/>.</summary>
[CollectionDefinition(Name)]
public sealed class MyBookingCollection : ICollectionFixture<MyBookingHost>
{
    public const string Name = "Ma réservation";
}
