using System.Globalization;
using System.Security.Cryptography;
using CarRental.Application.Bookings;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Pricing.Administration;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using CarRental.Domain.Referential;
using CarRental.Integration.Tests.Fleet;
using CarRental.Integration.Tests.Pricing;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Bookings;

/// <summary>
/// A test agency of its own (tests run in parallel on one database): an office and an airport, an ECMR and a CDMR
/// category with the requested vehicles (documents valid until 2030 unless said otherwise), and a published plan with a
/// base price of 100.000 (ECMR) and 150.000 (CDMR) per day and a child seat.
/// </summary>
internal sealed record BookingAgency(
    Guid AgencyId,
    Guid OfficeId,
    Guid AirportId,
    Guid Ecmr,
    Guid Cdmr,
    IReadOnlyList<Guid> EcmrVehicles,
    IReadOnlyList<Guid> CdmrVehicles);

internal static class BookingWorld
{
    private const string Letters = "abcdefghijklmnopqrstuvwxyz";

    public static UserContextSnapshot Agent { get; } = FleetData.Actor(Roles.Agent, "agent.dossiers@demo.local");

    public static UserContextSnapshot Manager { get; } = FleetData.Actor(Roles.Manager, "gerant.dossiers@demo.local");

    public static UserContextSnapshot Preparer { get; } = FleetData.Actor(Roles.Preparer, "preparateur.dossiers@demo.local");

    /// <summary>A Tunis instant <paramref name="days"/> days from today at <paramref name="hour"/>:00.</summary>
    public static DateTimeOffset InDays(int days, int hour = 10) =>
        TunisTime.FromLocal(TunisTime.Today(TimeProvider.System).AddDays(days), new TimeOnly(hour, 0));

    public static async Task<BookingAgency> CreateAsync(
        IntegrationHost host,
        CancellationToken cancellationToken,
        int ecmrVehicles = 1,
        int cdmrVehicles = 1,
        DateOnly? ecmrDocumentsExpireOn = null)
    {
        Guid agencyId;
        Guid officeId;
        Guid airportId;
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            var agency = Agency.Register(new AgencyDetails($"Agence dossiers {suffix}", "Tunis")).Value;
            var office = Location.Create(agency.Id, $"O{suffix}", "Agence", LocationKind.AgencyOffice).Value;
            var airport = Location.Create(agency.Id, $"A{suffix}", "Aéroport", LocationKind.Airport).Value;
            db.Set<Agency>().Add(agency);
            db.Set<Location>().AddRange(office, airport);
            await db.SaveChangesAsync(cancellationToken);
            (agencyId, officeId, airportId) = (agency.Id, office.Id, airport.Id);
        }

        var ecmr = await FleetData.NewCategoryAsync(host, agencyId, "ECMR", cancellationToken, displayOrder: 1);
        var cdmr = await FleetData.NewCategoryAsync(host, agencyId, "CDMR", cancellationToken, displayOrder: 2);
        var ecmrIds = new List<Guid>();
        for (var i = 0; i < ecmrVehicles; i++)
        {
            ecmrIds.Add(await FleetData.NewVehicleAsync(host, ecmr, cancellationToken, documentsExpireOn: i == 0 ? ecmrDocumentsExpireOn : null));
        }

        var cdmrIds = new List<Guid>();
        for (var i = 0; i < cdmrVehicles; i++)
        {
            cdmrIds.Add(await FleetData.NewVehicleAsync(host, cdmr, cancellationToken));
        }

        var draftId = await PricingScenario.AdministerAsync(host, s => s.CreateDraftAsync(new CreateDraftRequest(agencyId, null, "Grille dossiers"), cancellationToken));
        var detail = await PricingScenario.AdministerAsync(host, s => s.UpdatePricesAsync(
            draftId,
            0,
            [
                new CategoryPriceDefinition(ecmr, RatePeriodKey.Base, Money.Tnd(100m)),
                new CategoryPriceDefinition(cdmr, RatePeriodKey.Base, Money.Tnd(150m)),
            ],
            cancellationToken));
        detail = await PricingScenario.AdministerAsync(host, s => s.UpdateTiersAsync(draftId, detail.Plan.Revision, [new(1, 0m)], [], cancellationToken));
        detail = await PricingScenario.AdministerAsync(host, s => s.UpdateExtrasAsync(
            draftId,
            detail.Plan.Revision,
            [new ExtraDefinition("CHILD_SEAT", "Siège enfant", ExtraKind.Optional, ExtraPricingMode.PerDay, Money.Tnd(8m), Money.Tnd(50m), NameEn: "Child seat")],
            cancellationToken));
        await PricingScenario.AdministerAsync(host, s => s.PublishAsync(draftId, detail.Plan.Revision, cancellationToken));
        return new BookingAgency(agencyId, officeId, airportId, ecmr, cdmr, ecmrIds, cdmrIds);
    }

    /// <summary>An eligible Tunisian driver (CIN and a licence held for ten years), created by an agent.</summary>
    public static async Task<Guid> NewDriverAsync(IntegrationHost host, CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope(Agent);
        var registration = scope.ServiceProvider.GetRequiredService<ICustomerRegistration>();
        var today = TunisTime.Today(TimeProvider.System);
        var person = await registration.FindOrCreatePersonAsync(
            new PersonRegistrationRequest(
                Name(),
                Name(),
                "TN",
                new DateOnly(1990, 1, 15),
                RandomNumberGenerator.GetInt32(20_000_000, 100_000_000).ToString(CultureInfo.InvariantCulture),
                Document: new IdentityDocumentInput(
                    IdentityDocumentTypeInfo.NationalId,
                    RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture),
                    "TN",
                    today.AddYears(-5))),
            cancellationToken);
        person.IsSuccess.ShouldBeTrue(person.IsFailure ? person.Error.ToString() : null);
        var license = await registration.AddOrReplaceDocumentAsync(
            person.Value.PersonId,
            new IdentityDocumentInput(IdentityDocumentTypeInfo.DrivingLicense, $"{RandomNumberGenerator.GetInt32(10, 99)}/{RandomNumberGenerator.GetInt32(100_000, 999_999)}", "TN", today.AddYears(-10)),
            cancellationToken: cancellationToken);
        license.IsSuccess.ShouldBeTrue(license.IsFailure ? license.Error.ToString() : null);
        return person.Value.PersonId;
    }

    public static QuickBookingRequest Quick(
        BookingAgency agency,
        Guid driverId,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        QuickBookingOutcome outcome = QuickBookingOutcome.Confirmed,
        Guid? categoryId = null) => new(
        agency.AgencyId,
        SalesChannel.Phone,
        categoryId ?? agency.Ecmr,
        agency.OfficeId,
        agency.OfficeId,
        pickupAt,
        returnAt,
        driverId,
        outcome);

    /// <summary>Runs a call of a service in its own scope, as the back office does for each operation.</summary>
    public static async Task<T> RunAsync<TService, T>(IntegrationHost host, UserContextSnapshot actor, Func<TService, Task<T>> operation)
        where TService : notnull
    {
        await using var scope = host.CreateScope(actor);
        return await operation(scope.ServiceProvider.GetRequiredService<TService>());
    }

    /// <summary>A booking created by phone (as the agent), which must succeed.</summary>
    public static async Task<QuickBookingResult> CreateAsync(IntegrationHost host, QuickBookingRequest request, CancellationToken cancellationToken)
    {
        var created = await RunAsync<QuickBookingService, Result<QuickBookingResult>>(host, Agent, s => s.CreateAsync(request, cancellationToken));
        created.IsSuccess.ShouldBeTrue(created.IsFailure ? created.Error.ToString() : null);
        return created.Value;
    }

    /// <summary>Stores a setting value for the agency (as the Referential module would).</summary>
    public static async Task SetAsync(IntegrationHost host, Guid agencyId, string key, string value, CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        db.Set<Setting>().Add(Setting.Create(key, value, agencyId).Value);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Name()
    {
        Span<char> name = stackalloc char[9];
        for (var i = 0; i < name.Length; i++)
        {
            name[i] = Letters[RandomNumberGenerator.GetInt32(Letters.Length)];
        }

        name[0] = char.ToUpperInvariant(name[0]);
        return new string(name);
    }
}
