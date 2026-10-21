using System.Globalization;
using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Security;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Bookings.BookingWorld;
using QuoteLineKind = CarRental.Domain.Bookings.Quotes.QuoteLineKind;

namespace CarRental.Integration.Tests.Bookings;

/// <summary>The booking services on SQL Server with the real pricing, availability and customers modules.</summary>
public sealed class BookingServicesIntegrationTests(IntegrationHost host)
{
    private static readonly TimeSpan Buffer = TimeSpan.FromHours(2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_phone_booking_is_priced_checked_numbered_and_stored_with_its_quote_timeline_and_outbox_message()
    {
        var agency = await CreateAsync(host, Ct, ecmrVehicles: 2);
        var driver = await NewDriverAsync(host, Ct);
        var pickup = InDays(30);
        var request = Quick(agency, driver, pickup, pickup.AddDays(3)) with { Extras = [new SelectedExtra("CHILD_SEAT", 1)] };

        var created = await BookingWorld.CreateAsync(host, request, Ct);

        created.Number.ShouldMatch("^LOC-[0-9]{4}-[0-9]{6}$");
        created.Status.ShouldBe(BookingStatus.Confirmed);
        created.TotalInclVat.ShouldBe(Money.Tnd(331.140m), "3 days at 100.000, child seat 3 × 8.000, contribution 7.140");
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var booking = await db.Set<Booking>().AsNoTracking().SingleAsync(b => b.Id == created.BookingId, Ct);
        booking.ExtraSelection.ShouldBe("CHILD_SEAT:1");
        booking.PayerCustomerId.ShouldNotBeNull();
        var quote = await db.Set<Quote>().AsNoTracking().SingleAsync(q => q.BookingId == booking.Id, Ct);
        quote.Version.ShouldBe(1);
        quote.Lines.Select(l => l.Kind).ShouldBe([QuoteLineKind.Rental, QuoteLineKind.Extra, QuoteLineKind.DailyContribution]);
        quote.Lines.Sum(l => l.InclVat.Amount).ShouldBe(331.140m);
        quote.InputsJson.ShouldContain("\"categoryCode\":\"ECMR\"");
        var timeline = await db.Set<BookingEvent>().AsNoTracking().Where(e => e.BookingId == booking.Id).ToListAsync(Ct);
        timeline.ShouldHaveSingleItem().Type.ShouldBe(BookingEventType.Created);
        timeline[0].ActorName.ShouldBe(Agent.UserName);
        timeline[0].Channel.ShouldBe(SalesChannel.Phone);
        var messages = await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == typeof(BookingStatusChanged).FullName && m.Payload.Contains(booking.Id.ToString()))
            .CountAsync(Ct);
        messages.ShouldBe(1);
    }

    [Fact]
    public async Task The_occupancy_reader_returns_what_the_domain_computes_from_the_stored_files()
    {
        var agency = await CreateAsync(host, Ct, ecmrVehicles: 3);
        var driver = await NewDriverAsync(host, Ct);
        var inside = await BookingWorld.CreateAsync(host, Quick(agency, driver, InDays(20), InDays(23)), Ct);
        var touching = await BookingWorld.CreateAsync(host, Quick(agency, driver, InDays(10), InDays(15, hour: 9)), Ct);
        var after = await BookingWorld.CreateAsync(host, Quick(agency, driver, InDays(40), InDays(42)), Ct);
        var cancelled = await BookingWorld.CreateAsync(host, Quick(agency, driver, InDays(21), InDays(22)), Ct);
        (await RunAsync<BookingWorkflowService, Result<Money>>(host, Agent, s => s.CancelAsync(cancelled.BookingId, "Annulation", cancellationToken: Ct))).IsSuccess.ShouldBeTrue();
        var range = Period.Create(InDays(15), InDays(30)).Value;
        var basis = new OccupancyBasis(DateTimeOffset.UtcNow, Buffer);

        await using var scope = host.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IOccupancyReader>();
        var intervals = await reader.GetOccupancyAsync(agency.Ecmr, range, null, basis, Ct);

        intervals.Select(i => i.BookingId).ShouldBe([inside.BookingId, touching.BookingId], ignoreOrder: true, "the buffer of a return at 09:00 reaches into the range starting at 10:00");
        intervals.Single(i => i.BookingId == inside.BookingId).Period.ShouldBe(Period.Create(InDays(20), InDays(23) + Buffer).Value);
        (await reader.GetOccupancyAsync(agency.Ecmr, range, inside.BookingId, basis, Ct)).Select(i => i.BookingId).ShouldBe([touching.BookingId]);
        after.BookingId.ShouldNotBe(inside.BookingId);
    }

    [Fact]
    public async Task An_amendment_and_an_extension_create_new_versions_and_keep_the_previous_ones_unchanged()
    {
        var agency = await CreateAsync(host, Ct, ecmrVehicles: 1);
        var driver = await NewDriverAsync(host, Ct);
        var pickup = DateTimeOffset.UtcNow.AddMinutes(-5);
        var created = await BookingWorld.CreateAsync(host, Quick(agency, driver, pickup, pickup.AddDays(2)), Ct);
        AmendBookingRequest Current(BookingFileState state) => new(
            created.BookingId, agency.Ecmr, agency.OfficeId, agency.OfficeId, state.PickupAt, state.ReturnAt, [], null, null, driver, null, state.PayerCustomerId, null);

        var state = await StateAsync(created.BookingId);
        var amended = await RunAsync<BookingAmendmentService, Result<AmendmentResult>>(host, Agent, s => s.AmendAsync(Current(state) with { ReturnAt = state.ReturnAt.AddDays(1) }, Ct));
        amended.Value.QuoteVersion.ShouldBe(2);
        amended.Value.TotalInclVat.ShouldBe(Money.Tnd(307.140m));

        (await RunAsync<VehicleAssignmentService, Result>(host, Agent, s => s.AssignAsync(created.BookingId, agency.EcmrVehicles[0], cancellationToken: Ct))).IsSuccess.ShouldBeTrue();
        (await RunAsync<RentalLifecycleService, Result>(host, Preparer, s => s.StartRentalAsync(created.BookingId, cancellationToken: Ct))).IsSuccess.ShouldBeTrue();
        state = await StateAsync(created.BookingId);
        var extended = await RunAsync<BookingAmendmentService, Result<AmendmentResult>>(host, Agent, s => s.AmendAsync(Current(state) with { ReturnAt = state.ReturnAt.AddDays(2) }, Ct));
        extended.IsSuccess.ShouldBeTrue(extended.IsFailure ? extended.Error.ToString() : null);
        extended.Value.QuoteVersion.ShouldBe(3);

        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var versions = await db.Set<Quote>().AsNoTracking().Where(q => q.BookingId == created.BookingId).OrderBy(q => q.Version).ToListAsync(Ct);
        versions.Select(q => (q.Version, q.Origin)).ShouldBe([(1, QuoteOrigin.Initial), (2, QuoteOrigin.Requote), (3, QuoteOrigin.Extension)]);
        versions[0].TotalInclVat.ShouldBe(created.TotalInclVat, "version 1 is kept as it was frozen");
        versions[2].Lines.Select(l => l.Kind).ShouldBe([QuoteLineKind.Rental, QuoteLineKind.DailyContribution, QuoteLineKind.Extension, QuoteLineKind.DailyContribution]);
        versions[2].TotalInclVat.ShouldBe(Money.Tnd(307.140m + 200m + 4.760m));
        var history = await db.Set<Quote>().TemporalAll().AsNoTracking().Where(q => q.BookingId == created.BookingId).CountAsync(Ct);
        history.ShouldBe(3, "a quote row is never updated: no history row beyond the three versions");
        var booking = await db.Set<Booking>().AsNoTracking().SingleAsync(b => b.Id == created.BookingId, Ct);
        booking.CurrentQuoteVersion.ShouldBe(3);
        booking.Status.ShouldBe(BookingStatus.InProgress);
    }

    [Fact]
    public async Task An_expired_request_frees_its_capacity()
    {
        var agency = await CreateAsync(host, Ct, ecmrVehicles: 1);
        await SetAsync(host, agency.AgencyId, "bookings.hold_duration", "00:00:05", Ct);
        var webDriver = await NewDriverAsync(host, Ct);
        var phoneDriver = await NewDriverAsync(host, Ct);
        var pickup = InDays(35);
        var submitted = await RunAsync<IBookingRequests, Result<BookingRequestResult>>(
            host,
            UserContextSnapshot.System("system:public-funnel"),
            s => s.SubmitRequestAsync(
                new BookingRequestDraft(SalesChannel.Web, Price: new PriceRequest(agency.AgencyId, agency.Ecmr, agency.OfficeId, agency.OfficeId, pickup, pickup.AddDays(2), SalesChannel.Web)),
                new BookingRequestParties(webDriver),
                Ct));
        submitted.IsSuccess.ShouldBeTrue(submitted.IsFailure ? submitted.Error.ToString() : null);
        (await RunAsync<QuickBookingService, Result<QuickBookingResult>>(host, Agent, s => s.CreateAsync(Quick(agency, phoneDriver, pickup, pickup.AddDays(2)), Ct)))
            .Error.ShouldBe(AvailabilityErrors.NotAvailable, "the request holds the last vehicle");

        await WaitUntilAsync(() => DateTimeOffset.UtcNow > submitted.Value.HoldExpiresAt!.Value);
        await RunJobAsync("bookings.expire");

        (await StatusAsync(submitted.Value.BookingId)).ShouldBe(BookingStatus.Expired);
        (await RunAsync<QuickBookingService, Result<QuickBookingResult>>(host, Agent, s => s.CreateAsync(Quick(agency, phoneDriver, pickup, pickup.AddDays(2)), Ct)))
            .IsSuccess.ShouldBeTrue("the capacity was released");
    }

    [Fact]
    public async Task A_vehicle_already_carrying_a_file_within_the_preparation_buffer_is_refused()
    {
        var agency = await CreateAsync(host, Ct, ecmrVehicles: 2);
        var driver = await NewDriverAsync(host, Ct);
        var first = await BookingWorld.CreateAsync(host, Quick(agency, driver, InDays(25), InDays(27)), Ct);
        var second = await BookingWorld.CreateAsync(host, Quick(agency, driver, InDays(27, hour: 11), InDays(29)), Ct);
        (await RunAsync<VehicleAssignmentService, Result>(host, Agent, s => s.AssignAsync(first.BookingId, agency.EcmrVehicles[0], cancellationToken: Ct))).IsSuccess.ShouldBeTrue();

        var onSameVehicle = await RunAsync<VehicleAssignmentService, Result>(host, Agent, s => s.AssignAsync(second.BookingId, agency.EcmrVehicles[0], cancellationToken: Ct));

        onSameVehicle.Error.ShouldBe(AvailabilityErrors.VehicleOverlap, "the return at 10:00 plus 2 h of preparation reaches 11:00");
        (await RunAsync<VehicleAssignmentService, Result>(host, Agent, s => s.AssignAsync(second.BookingId, agency.EcmrVehicles[1], cancellationToken: Ct))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_vehicle_whose_documents_expire_before_the_return_needs_a_traced_waiver_of_a_manager()
    {
        var expiresOn = TunisTime.Today(TimeProvider.System).AddDays(32);
        var agency = await CreateAsync(host, Ct, ecmrVehicles: 2, ecmrDocumentsExpireOn: expiresOn);
        var driver = await NewDriverAsync(host, Ct);
        var booking = await BookingWorld.CreateAsync(host, Quick(agency, driver, InDays(31), InDays(34)), Ct);
        var expiring = agency.EcmrVehicles[0];

        (await RunAsync<VehicleAssignmentService, Result>(host, Agent, s => s.AssignAsync(booking.BookingId, expiring, cancellationToken: Ct)))
            .Error.ShouldBe(AvailabilityErrors.VehicleNotCompliant);
        (await RunAsync<VehicleAssignmentService, Result>(host, Agent, s => s.AssignAsync(booking.BookingId, expiring, "Assurance renouvelée", cancellationToken: Ct)))
            .Error.ShouldBe(BookingErrors.Forbidden);
        (await RunAsync<VehicleAssignmentService, Result>(host, Manager, s => s.AssignAsync(booking.BookingId, expiring, "Assurance renouvelée, attestation attendue demain", cancellationToken: Ct)))
            .IsSuccess.ShouldBeTrue();

        await using var scope = host.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Booking>().AsNoTracking().SingleAsync(b => b.Id == booking.BookingId, Ct);
        stored.AssignedVehicleId.ShouldBe(expiring);
        stored.ComplianceWaiver.ShouldNotBeNull().ByName.ShouldBe(Manager.UserName);
        stored.ComplianceWaiver.Reason.ShouldBe("Assurance renouvelée, attestation attendue demain");
        stored.IsComplianceWaived.ShouldBeTrue();
    }

    [Fact]
    public async Task The_closing_restarts_the_retention_of_the_document_images_of_the_drivers()
    {
        var agency = await CreateAsync(host, Ct, ecmrVehicles: 1);
        var driver = await NewDriverAsync(host, Ct);
        var pickup = DateTimeOffset.UtcNow.AddMinutes(-10);
        var created = await BookingWorld.CreateAsync(host, Quick(agency, driver, pickup, pickup.AddDays(1)), Ct);
        (await RunAsync<VehicleAssignmentService, Result>(host, Agent, s => s.AssignAsync(created.BookingId, agency.EcmrVehicles[0], cancellationToken: Ct))).IsSuccess.ShouldBeTrue();
        (await RunAsync<RentalLifecycleService, Result>(host, Preparer, s => s.StartRentalAsync(created.BookingId, cancellationToken: Ct))).IsSuccess.ShouldBeTrue();
        (await RunAsync<IBookingsQueries, bool>(host, Agent, q => q.HasOpenBookingsAsync(driver, Ct))).ShouldBeTrue();
        (await RunAsync<RentalLifecycleService, Result>(host, Preparer, s => s.CompleteReturnAsync(created.BookingId, cancellationToken: Ct))).IsSuccess.ShouldBeTrue();

        (await RunAsync<RentalLifecycleService, Result>(host, Agent, s => s.CloseAsync(created.BookingId, cancellationToken: Ct))).IsSuccess.ShouldBeTrue();

        await using var scope = host.CreateScope();
        var person = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Person>().AsNoTracking().SingleAsync(p => p.Id == driver, Ct);
        person.LastRentalClosedAtUtc.ShouldNotBeNull();
        (await RunAsync<IBookingsQueries, bool>(host, Agent, q => q.HasOpenBookingsAsync(driver, Ct))).ShouldBeFalse();
        (await StatusAsync(created.BookingId)).ShouldBe(BookingStatus.Closed);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline);
            await Task.Delay(100, Ct);
        }
    }

    private async Task RunJobAsync(string jobId)
    {
        await using var scope = host.CreateScope(UserContextSnapshot.System(string.Create(CultureInfo.InvariantCulture, $"system:job:{jobId}")));
        var descriptor = scope.ServiceProvider.GetServices<RecurringJobDescriptor>().Single(d => d.JobId == jobId);
        await ((IRecurringJob)scope.ServiceProvider.GetRequiredService(descriptor.JobType)).ExecuteAsync(Ct);
    }

    private async Task<BookingStatus> StatusAsync(Guid bookingId)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Booking>().Where(b => b.Id == bookingId).Select(b => b.Status).SingleAsync(Ct);
    }

    private async Task<BookingFileState> StateAsync(Guid bookingId)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Booking>()
            .Where(b => b.Id == bookingId)
            .Select(b => new BookingFileState(b.PickupAt, b.ReturnAt, b.PayerCustomerId))
            .SingleAsync(Ct);
    }

    private sealed record BookingFileState(DateTimeOffset PickupAt, DateTimeOffset ReturnAt, Guid? PayerCustomerId);
}
