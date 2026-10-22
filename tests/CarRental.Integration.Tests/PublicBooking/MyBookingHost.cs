using CarRental.Application.Common.Persistence;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Referential;
using CarRental.Integration.Tests.Bookings;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.PublicBooking;

/// <summary>
/// The host of "Ma réservation" over HTTP (ADR-0051): the site of an agency finds the bookings of its own agency only
/// (ADR-0067), and the main demo agency of the shared host has settings the other tests rely on. This host has a
/// database of its own without the demo data: its site is the site of <see cref="Agency"/>, created at start-up, at the
/// address <see cref="Site"/>. A scenario changes a setting of that agency with <see cref="SetAsync"/>, which takes it
/// back at the end: the tests of the collection run one after the other.
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

    /// <summary>The first segment of the addresses of the site of <see cref="Agency"/>: <c>/{slug}</c>.</summary>
    public string Site { get; private set; } = string.Empty;

    /// <summary>The name of <see cref="Agency"/>, the brand of its site and of the e-mails to its customers.</summary>
    public string AgencyName { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        // Starts the host (migrations and reference data), then creates the agency of its site.
        _ = Factory.Server;
        Agency = await BookingWorld.CreateAsync(Host, CancellationToken.None, ecmrVehicles: 3);
        var site = await Factory.Services.GetRequiredService<IAgencyDirectory>().FindAsync(Agency.AgencyId);
        Site = "/" + site.ShouldNotBeNull().Slug;
        AgencyName = site.Name;
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
