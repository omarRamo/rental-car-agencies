using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Fleet;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using CarRental.Integration.Tests.Fleet;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Reporting.ReportingWorld;

namespace CarRental.Integration.Tests.Reporting;

/// <summary>
/// The read contracts the Reporting module added to the Bookings, Fleet and Payments modules (ADR-0045), on SQL Server:
/// what the snapshots and the indicators are computed from.
/// </summary>
public sealed class ReportingSourceContractTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_bookings_contract_gives_the_quote_totals_the_vehicles_and_the_timeline_of_the_files()
    {
        var agency = await CreateAsync(host, vehicles: 2, Ct);
        var swapped = await PastFileAsync(host, agency, agency.Vehicles[0], At(-9), days: 4, rentalInclVat: 476m, PastOutcome.Closed, Ct, swapTo: agency.Vehicles[1]);
        var cancelled = await PastFileAsync(host, agency, agency.Vehicles[0], At(-3), days: 2, rentalInclVat: 238m, PastOutcome.Cancelled, Ct, cancellationFee: Money.Tnd(47.6m));
        var later = await PastFileAsync(host, agency, agency.Vehicles[1], At(-40), days: 2, rentalInclVat: 238m, PastOutcome.Closed, Ct);
        var other = await CreateAsync(host, vehicles: 1, Ct);
        await PastFileAsync(host, other, other.Vehicles[0], At(-9), days: 3, rentalInclVat: 357m, PastOutcome.Closed, Ct);

        await using var scope = host.CreateScope();
        var facts = await scope.ServiceProvider.GetRequiredService<IBookingsReportingQueries>().GetBookingsOverlappingAsync(agency.AgencyId, Day(-10), Day(-1), Ct);

        facts.Select(f => f.BookingId).ShouldBe([swapped.Id, cancelled.Id]);
        var closed = facts[0];
        closed.Number.ShouldBe(swapped.Number);
        closed.Status.ShouldBe(BookingStatusInfo.Closed);
        closed.Channel.ShouldBe(SalesChannel.Phone);
        closed.CategoryId.ShouldBe(agency.CategoryId);
        closed.PickupAt.ShouldBe(At(-9));
        closed.Quote.ShouldSatisfyAllConditions(
            q => q.Days.ShouldBe(4),
            q => q.Version.ShouldBe(1),
            q => q.TotalExclVat.ShouldBe(Money.Tnd(408m)),
            q => q.DailyContributionExclVat.ShouldBe(Money.Tnd(8m)),
            q => q.TotalInclVat.ShouldBe(Money.Tnd(485.52m)));
        closed.Vehicles.ShouldBe([new BookingVehicleSpanInfo(agency.Vehicles[0], null), new BookingVehicleSpanInfo(agency.Vehicles[1], At(-8, 14))]);
        closed.StatusHistory.Select(h => (h.From, h.To)).ShouldBe(
        [
            (null, BookingStatusInfo.Confirmed),
            (BookingStatusInfo.Confirmed, BookingStatusInfo.InProgress),
            (BookingStatusInfo.InProgress, BookingStatusInfo.Returned),
            (BookingStatusInfo.Returned, BookingStatusInfo.Closed),
        ]);
        closed.CancellationFee.ShouldBeNull();
        facts[1].ShouldSatisfyAllConditions(
            f => f.Status.ShouldBe(BookingStatusInfo.Cancelled),
            f => f.CancellationFee.ShouldBe(Money.Tnd(47.6m)),
            f => f.Vehicles.ShouldBe([new BookingVehicleSpanInfo(agency.Vehicles[0], null)]));
        later.Id.ShouldNotBeOneOf([.. facts.Select(f => f.BookingId)]);
    }

    [Fact]
    public async Task The_bookings_contract_lists_the_requests_by_submission_with_their_timeline()
    {
        var agency = await CreateAsync(host, vehicles: 1, Ct);
        var submittedAt = At(-6, 9);
        var request = await PastRequestAsync(agency, submittedAt, At(5));

        await using var scope = host.CreateScope();
        var queries = scope.ServiceProvider.GetRequiredService<IBookingsReportingQueries>();
        var within = await queries.GetRequestsSubmittedAsync(agency.AgencyId, At(-7, 0), At(-5, 0), Ct);
        var outside = await queries.GetRequestsSubmittedAsync(agency.AgencyId, At(-5, 0), At(0, 0), Ct);

        var found = within.ShouldHaveSingleItem();
        found.BookingId.ShouldBe(request.Id);
        found.Channel.ShouldBe(SalesChannel.Web);
        found.Source.ShouldBe("google");
        found.SubmittedAtUtc.ShouldBe(submittedAt);
        found.StatusHistory.Select(h => (h.AtUtc, h.To)).ShouldBe(
        [
            (submittedAt.AddMinutes(-10), BookingStatusInfo.Draft),
            (submittedAt, BookingStatusInfo.PendingValidation),
            (submittedAt.AddMinutes(95), BookingStatusInfo.Confirmed),
        ]);
        outside.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_fleet_contract_judges_past_days_on_the_documents_valid_then_and_gives_the_depreciation()
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var category = await FleetData.NewCategoryAsync(host, agencyId, "ECMR", Ct);
        var vehicle = await FleetData.NewVehicleAsync(host, category, Ct, withDocuments: false);
        await using (var scope = host.CreateScope(FleetData.Manager))
        {
            var vehicles = scope.ServiceProvider.GetRequiredService<VehicleService>();
            foreach (var type in VehicleCompliance.MandatoryTypes)
            {
                // Expired six days ago, renewed from three days ago: not compliant the two days in between.
                Ok(await vehicles.RecordDocumentAsync(vehicle, new DocumentForm(type, $"OLD-{type}", null, Day(-6), null, type == VehicleDocumentType.Insurance ? 365m : null, null), Ct));
                Ok(await vehicles.RecordDocumentAsync(vehicle, new DocumentForm(type, $"NEW-{type}", Day(-3), Day(360), null, null, null), Ct));
            }

            // Bought 400 days ago, depreciated over two years: 100.000 per day whatever the leap years.
            var purchasedOn = Day(-400);
            var depreciationDays = purchasedOn.AddYears(2).DayNumber - purchasedOn.DayNumber;
            Ok(await vehicles.SetEconomicsAsync(vehicle, new EconomicsForm(100m * depreciationDays, purchasedOn, FinancingType.Cash, null, null, 2, 0m), Ct));
            Ok(await vehicles.AddCostAsync(vehicle, new CostForm(Day(-4), "Lavage complet", 80m), Ct));
        }

        await using var read = host.CreateScope();
        var fleet = read.ServiceProvider.GetRequiredService<IFleetReportingQueries>();
        var history = (await fleet.GetVehicleDaysAsync(agencyId, Day(-7), Day(-1), Ct)).ShouldHaveSingleItem();
        var costs = await fleet.GetVehicleCostsAsync(agencyId, Day(-7), Day(-1), Ct);

        history.VehicleId.ShouldBe(vehicle);
        history.Days.Select(d => (d.Day, d.IsRentable)).ShouldBe(
        [
            (Day(-7), true),
            (Day(-6), true),
            (Day(-5), false),
            (Day(-4), false),
            (Day(-3), true),
            (Day(-2), true),
            (Day(-1), true),
        ]);
        history.Days.ShouldAllBe(d => d.CategoryId == category && d.CategoryCode == "ECMR" && d.Depreciation == Money.Tnd(100m));
        // The premium of the old policy, recorded without issue date, covers the year before its expiry.
        costs.Select(c => (c.VehicleId, c.Kind, c.Amount, c.From, c.To)).ShouldBe(
            [
                (vehicle, FleetCostKindInfo.Document, Money.Tnd(365m), Day(-6).AddYears(-1).AddDays(1), Day(-6)),
                (vehicle, FleetCostKindInfo.Other, Money.Tnd(80m), Day(-4), Day(-4)),
            ],
            ignoreOrder: true);
    }

    [Fact]
    public async Task The_fleet_contract_stops_at_the_sale_and_blocks_from_the_current_out_of_service_status()
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var category = await FleetData.NewCategoryAsync(host, agencyId, "ECMR", Ct);
        var sold = await FleetData.NewVehicleAsync(host, category, Ct);
        var broken = await FleetData.NewVehicleAsync(host, category, Ct);
        await using (var scope = host.CreateScope(FleetData.Manager))
        {
            var vehicles = scope.ServiceProvider.GetRequiredService<VehicleService>();
            Ok(await vehicles.SellAsync(sold, new SaleForm(At(-2, 12), 30_000m, "Acheteur"), Ct));
            Ok(await vehicles.ChangeStatusAsync(broken, VehicleStatus.OutOfService, "Accident", Ct));
        }

        await using var read = host.CreateScope();
        var days = await read.ServiceProvider.GetRequiredService<IFleetReportingQueries>().GetVehicleDaysAsync(agencyId, Day(-3), Day(2), Ct);

        days.Single(v => v.VehicleId == sold).Days.Select(d => d.Day).ShouldBe([Day(-3), Day(-2)]);
        days.Single(v => v.VehicleId == broken).Days.Select(d => (d.Day, d.IsRentable)).ShouldBe(
        [
            (Day(-3), true),
            (Day(-2), true),
            (Day(-1), true),
            (Day(0), true),
            (Day(1), false),
            (Day(2), false),
        ]);
    }

    [Fact]
    public async Task The_payments_contract_totals_the_payments_of_many_bookings_at_once()
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var paid = Guid.CreateVersion7();
        var refunded = Guid.CreateVersion7();
        await using (var scope = host.CreateScope(Agent))
        {
            var payments = scope.ServiceProvider.GetRequiredService<ICounterPayments>();
            Ok(await payments.RecordPaymentAsync(new CounterPaymentRequest(paid, agencyId, PaymentMethodInfo.CardTerminal, 100m), Ct));
            var payment = await payments.RecordPaymentAsync(new CounterPaymentRequest(refunded, agencyId, PaymentMethodInfo.CardTerminal, 50m), Ct);
            Ok(payment);
            Ok(await payments.RecordRefundAsync(new CounterRefundRequest(payment.Value, PaymentMethodInfo.CardTerminal, 20m, "Geste commercial"), Ct));
        }

        await using var read = host.CreateScope();
        var totals = await read.ServiceProvider.GetRequiredService<IPaymentsReportingQueries>()
            .GetPaymentTotalsByBookingAsync([paid, refunded, Guid.CreateVersion7()], Ct);

        totals.Count.ShouldBe(2);
        totals[paid].NetPaid.ShouldBe(Money.Tnd(100m));
        totals[refunded].ShouldSatisfyAllConditions(t => t.Captured.ShouldBe(Money.Tnd(50m)), t => t.Refunded.ShouldBe(Money.Tnd(20m)), t => t.NetPaid.ShouldBe(Money.Tnd(30m)));
    }

    /// <summary>A web request created as a draft, submitted at <paramref name="submittedAt"/> and validated 95 minutes later.</summary>
    private async Task<Booking> PastRequestAsync(ReportingAgency agency, DateTimeOffset submittedAt, DateTimeOffset pickupAt)
    {
        var id = Entity.NewId();
        var createdAt = submittedAt.AddMinutes(-10);
        var lines = new QuoteLineData[]
        {
            new(QuoteLineKind.Rental, "RENTAL", "Pricing.Line.Rental", [], 3, Money.Tnd(119m), Money.Tnd(300m), Money.Tnd(57m), Money.Tnd(357m), 0.19m),
        };
        var location = Guid.CreateVersion7();
        var content = new QuoteContent(agency.AgencyId, agency.CategoryId, SalesChannel.Web, location, location, pickupAt, pickupAt.AddDays(3), Guid.CreateVersion7(), 1, 3, lines, Money.Tnd(300m), Money.Tnd(57m), Money.Tnd(357m), Money.Tnd(357m), Money.Tnd(600m), false, QuotePromoCodeOutcome.None, null, "[]", "{}", "[]");
        var quote = Quote.CreateVersion(id, 1, QuoteOrigin.Initial, content, QuoteCode.Generate(), "tests", createdAt).Value;
        var number = DocumentNumber.Create(DocumentType.Booking, Random.Shared.Next(2001, 2100), Random.Shared.NextInt64(1, 999_999)).Value;
        var customer = new BookingActor(null, "client", SalesChannel.Web);
        var details = new BookingDetails(
            agency.AgencyId,
            SalesChannel.Web,
            agency.CategoryId,
            location,
            location,
            pickupAt,
            pickupAt.AddDays(3),
            BookingParties.None,
            Attribution: BookingAttribution.Create("google").Value);
        var booking = Booking.CreateDraft(id, details, number, quote.ToReference(), customer, createdAt).Value;
        Ok(booking.Submit(new BookingParties(Guid.CreateVersion7(), null, Guid.CreateVersion7()), TimeSpan.FromHours(24), customer, submittedAt));
        Ok(booking.Validate(null, new BookingActor(Manager.UserId, Manager.UserName), submittedAt.AddMinutes(95)));

        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        db.Set<Booking>().Add(booking);
        db.Set<Quote>().Add(quote);
        await db.SaveChangesAsync(Ct);
        return booking;
    }

    private static void Ok(Result result) => result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
}
