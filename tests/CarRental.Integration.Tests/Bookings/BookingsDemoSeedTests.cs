using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Pricing.Administration;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using CarRental.Domain.Pricing;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Fleet;
using CarRental.Integration.Tests.Pricing;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Bookings;

/// <summary>The demo rental files seeded at startup (Seed:Demo is on in the test host).</summary>
public sealed class BookingsDemoSeedTests(IntegrationHost host)
{
    private const string DemoAgencyName = "Agence Démo Tunis";

    private const string SecondDemoAgencyName = "Agence Démo Sousse";

    private static readonly string[] SecondAgencyEmails = ["hichem.mabrouk@example.com", "ines.zouari@example.com", "marco.bianchi@example.net"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_demo_seed_creates_files_in_every_status_and_never_overbooks_a_vehicle_or_a_category()
    {
        await using var scope = host.CreateScope();
        var services = scope.ServiceProvider;
        var agency = (await services.GetRequiredService<IReferentialQueries>().GetAgenciesAsync(Ct)).Single(a => a.Name == DemoAgencyName);
        var db = services.GetRequiredService<CarRentalDbContext>();
        var files = await db.Set<Booking>().AsNoTracking().Where(b => b.AgencyId == agency.Id && b.Attribution.Source == "demo").ToListAsync(Ct);
        var now = TimeProvider.System.GetUtcNow();

        files.Count.ShouldBe(41, string.Join(", ", files.GroupBy(f => f.Status).Select(g => $"{g.Key}={g.Count()}")));
        files.Select(f => f.Status).Distinct().Order().ShouldBe(Enum.GetValues<BookingStatus>());
        files.ShouldContain(f => f.IsLateAt(now));
        files.ShouldContain(f => f.GuaranteedVehicleId != null && f.AssignedVehicleId == f.GuaranteedVehicleId);
        files.ShouldContain(f => f.IsComplianceWaived);
        files.ShouldContain(f => f.Status == BookingStatus.Cancelled && f.CancellationFee!.Value.IsPositive);
        files.ShouldContain(f => f.Status == BookingStatus.Refused && f.StatusReason == BookingReasons.CustomerCheck);
        files.Count(f => f.Status == BookingStatus.Confirmed && f.AssignedVehicleId == null && f.PickupAt < now.AddHours(48)).ShouldBeGreaterThanOrEqualTo(2);
        files.ShouldAllBe(f => f.PickupAt > now.AddDays(-31) && f.PickupAt < now.AddDays(61));
        var quotes = await db.Set<Quote>().AsNoTracking().Where(q => q.BookingId != null && files.Select(f => f.Id).Contains(q.BookingId.Value)).ToListAsync(Ct);
        quotes.ShouldContain(q => q.Origin == QuoteOrigin.Requote);
        quotes.ShouldContain(q => q.Origin == QuoteOrigin.ManualDiscount && q.ManualDiscountReason != null);

        // The history is numbered first, in chronological order, then the files created at seeding time.
        var numbers = files.OrderBy(f => f.CreatedAtUtc).Select(f => f.Number).ToList();
        numbers.ShouldBe([.. numbers.Order(StringComparer.Ordinal)]);

        // No vehicle carries two files at once, preparation buffer included (past included: the guard protects the future).
        var buffer = await services.GetRequiredService<ISettingsReader>().GetAsync(WellKnownSettings.PreparationBuffer, agency.Id, Ct);
        var basis = new OccupancyBasis(now, buffer);
        var everything = await db.Set<Booking>().AsNoTracking().Where(b => b.AgencyId == agency.Id).ToListAsync(Ct);
        var occupancy = everything
            .Select(b => (Booking: b, Period: basis.PeriodOf(b.ToOccupancy())))
            .Where(x => x.Period is not null)
            .Select(x => (x.Booking, Period: x.Period!))
            .ToList();
        foreach (var vehicle in occupancy.Where(x => x.Booking.AssignedVehicleId is not null).GroupBy(x => x.Booking.AssignedVehicleId))
        {
            var periods = vehicle.OrderBy(x => x.Period.Start).ToList();
            for (var i = 1; i < periods.Count; i++)
            {
                periods[i].Period.Overlaps(periods[i - 1].Period).ShouldBeFalse($"{periods[i - 1].Booking.Number} and {periods[i].Booking.Number} share a vehicle.");
            }
        }

        // No category ever carries more files than vehicles, and from now on its capacity profile has no shortfall.
        var fleet = services.GetRequiredService<IFleetQueries>();
        var availability = services.GetRequiredService<IAvailabilityQueries>();
        foreach (var category in await fleet.GetCategoriesAsync(agency.Id, cancellationToken: Ct))
        {
            var vehicles = (await fleet.GetVehiclesAsync(category.Id, Ct)).Count;
            MaxConcurrency(occupancy.Where(x => x.Booking.CategoryId == category.Id).Select(x => x.Period)).ShouldBeLessThanOrEqualTo(vehicles, category.AcrissCode);
            var profile = await availability.GetCapacityProfileAsync(category.Id, Period.Create(now, now.AddDays(62)).Value, cancellationToken: Ct);
            profile.ShouldNotBeNull();
            profile.ShouldAllBe(segment => segment.Remaining >= 0, category.AcrissCode);
        }

        // The seed step is idempotent.
        var seeder = services.GetServices<IDataSeeder>().Single(s => s.Stage == SeedStage.Demo && s.Order == 700);
        await seeder.SeedAsync(Ct);
        (await db.Set<Booking>().CountAsync(b => b.AgencyId == agency.Id && b.Attribution.Source == "demo", Ct)).ShouldBe(files.Count);
    }

    /// <summary>
    /// The second demo agency (ADR-0067): its three demo customers are its own and, once its fleet and its plan exist
    /// (demo data of the Fleet and Pricing modules, given here when missing), a closed rental, a rental in progress and a
    /// confirmed booking of its own, driven by its customers, that the main agency never sees.
    /// </summary>
    [Fact]
    public async Task The_second_demo_agency_gets_customers_and_files_of_its_own()
    {
        Guid main;
        Guid second;
        await using (var scope = host.CreateScope())
        {
            var agencies = await scope.ServiceProvider.GetRequiredService<IReferentialQueries>().GetAgenciesAsync(Ct);
            main = agencies.Single(a => a.Name == DemoAgencyName).Id;
            second = agencies.Single(a => a.Name == SecondDemoAgencyName).Id;
        }

        await EquipWhenMissingAsync(second);
        await using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetServices<IDataSeeder>().Single(s => s.Stage == SeedStage.Demo && s.Order == 700).SeedAsync(Ct);
        }

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var customers = await db.Set<Person>().AsNoTracking().Where(p => p.AgencyId == second && SecondAgencyEmails.Contains(p.Email!)).Select(p => p.Id).ToListAsync(Ct);
        customers.Count.ShouldBe(3);
        var files = await db.Set<Booking>().AsNoTracking().Where(b => b.AgencyId == second && b.Attribution.Source == "demo").ToListAsync(Ct);
        files.Select(f => f.Status).ShouldBe([BookingStatus.Closed, BookingStatus.InProgress, BookingStatus.Confirmed], ignoreOrder: true);
        files.ShouldAllBe(f => f.MainDriverId != null && customers.Contains(f.MainDriverId.Value));
        files.Where(f => f.Status != BookingStatus.Confirmed).ShouldAllBe(f => f.AssignedVehicleId != null);

        await using var staff = host.CreateScope(TestActors.Staff(main, Roles.Manager));
        (await staff.ServiceProvider.GetRequiredService<ICustomersQueries>().SearchPersonsAsync(SecondAgencyEmails[0], cancellationToken: Ct)).ShouldBeEmpty();
        foreach (var file in files)
        {
            (await staff.ServiceProvider.GetRequiredService<IBookingsQueries>().GetSummaryAsync(file.Id, Ct)).ShouldBeNull();
        }
    }

    /// <summary>An ECMR and a CDMR category with two vehicles each and a published plan, unless the agency has a plan.</summary>
    private async Task EquipWhenMissingAsync(Guid agencyId)
    {
        await using (var scope = host.CreateScope())
        {
            if (await scope.ServiceProvider.GetRequiredService<IPricingQueries>().GetPublishedPlanAsync(agencyId, channel: null, Ct) is not null)
            {
                return;
            }
        }

        var ecmr = await FleetData.NewCategoryAsync(host, agencyId, "ECMR", Ct, displayOrder: 1);
        var cdmr = await FleetData.NewCategoryAsync(host, agencyId, "CDMR", Ct, displayOrder: 2);
        for (var i = 0; i < 2; i++)
        {
            await FleetData.NewVehicleAsync(host, ecmr, Ct);
            await FleetData.NewVehicleAsync(host, cdmr, Ct);
        }

        var draftId = await PricingScenario.AdministerAsync(host, s => s.CreateDraftAsync(new CreateDraftRequest(agencyId, null, "Grille Sousse"), Ct));
        var detail = await PricingScenario.AdministerAsync(host, s => s.UpdatePricesAsync(
            draftId,
            0,
            [new CategoryPriceDefinition(ecmr, RatePeriodKey.Base, Money.Tnd(95m)), new CategoryPriceDefinition(cdmr, RatePeriodKey.Base, Money.Tnd(140m))],
            Ct));
        detail = await PricingScenario.AdministerAsync(host, s => s.UpdateTiersAsync(draftId, detail.Plan.Revision, [new(1, 0m)], [], Ct));
        await PricingScenario.AdministerAsync(host, s => s.PublishAsync(draftId, detail.Plan.Revision, Ct));
    }

    /// <summary>The largest number of periods covering one instant.</summary>
    private static int MaxConcurrency(IEnumerable<Period> periods)
    {
        var edges = periods
            .SelectMany(p => new[] { (At: p.Start, Delta: 1), (At: p.End, Delta: -1) })
            .OrderBy(e => e.At)
            .ThenBy(e => e.Delta)
            .ToList();
        var current = 0;
        var max = 0;
        foreach (var edge in edges)
        {
            current += edge.Delta;
            max = Math.Max(max, current);
        }

        return max;
    }
}
