using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Administration;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Handover;
using CarRental.Application.Bookings.PublicAccess;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Access;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Bookings.Handover;
using CarRental.Integration.Tests.Fleet;
using CarRental.Integration.Tests.PublicBooking;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Bookings.BookingWorld;

namespace CarRental.Integration.Tests.Bookings;

/// <summary>
/// The rental files of an agency are its own (ADR-0067), on SQL Server: the staff of another agency never lists, reads
/// nor changes a file, its quotes or its hand-overs; numbers and channel references are unique within an agency only;
/// "Ma réservation" and the booking funnel of the site of an agency never reach a file or a session of another agency.
/// </summary>
public sealed class BookingAgencyIsolationTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_staff_of_an_agency_never_lists_reads_nor_changes_the_files_of_another_agency()
    {
        var (first, second) = (await CreateAsync(host, Ct), await CreateAsync(host, Ct));
        var ours = await CreateAsync(host, Quick(first, await NewDriverAsync(host, first.AgencyId, Ct), InDays(30), InDays(32)), Ct);
        var theirDriver = await NewDriverAsync(host, second.AgencyId, Ct);
        var theirs = await CreateAsync(host, Quick(second, theirDriver, InDays(30), InDays(32)), Ct);
        var staff = TestActors.Staff(first.AgencyId, Roles.Manager);

        theirs.Number.ShouldBe(ours.Number, "each agency numbers its own files");
        await using (var scope = host.CreateScope(staff))
        {
            var queries = scope.ServiceProvider.GetRequiredService<IBookingsQueries>();
            (await queries.GetSummaryAsync(theirs.BookingId, Ct)).ShouldBeNull();
            (await queries.FindByNumberAsync(null, theirs.Number, Ct)).ShouldNotBeNull().Id.ShouldBe(ours.BookingId, "a number designates a file of the agency of the operation");
            (await queries.FindByNumberAsync(second.AgencyId, theirs.Number, Ct)).ShouldBeNull("never a file of another agency");
            (await queries.GetBookingsOfPersonAsync(theirDriver, Ct)).ShouldBeEmpty();
            var administration = scope.ServiceProvider.GetRequiredService<BookingAdministrationQueries>();
            (await administration.ListAsync(new BookingListFilter(Search: theirs.Number), new PageRequest(), Ct)).Value.Items.Select(i => i.Id).ShouldBe([ours.BookingId]);
            (await administration.ListAsync(new BookingListFilter(AgencyId: second.AgencyId), new PageRequest(), Ct)).Value.Items.ShouldBeEmpty();
            (await administration.GetFileAsync(theirs.BookingId, Ct)).Error.ShouldBe(BookingErrors.NotFound);
            (await administration.GetQuoteVersionAsync(theirs.BookingId, 1, Ct)).Error.ShouldBe(BookingErrors.QuoteNotFound);
            (await administration.GetRequestQueueAsync(second.AgencyId, Ct)).Value.ShouldBeEmpty();
        }

        (await RunAsync<BookingWorkflowService, Result<Money>>(host, staff, s => s.CancelAsync(theirs.BookingId, "Essai d'une autre agence", cancellationToken: Ct)))
            .Error.ShouldBe(BookingErrors.NotFound);
        (await RunAsync<VehicleAssignmentService, Result>(host, staff, s => s.AssignAsync(theirs.BookingId, second.EcmrVehicles[0], cancellationToken: Ct)))
            .Error.ShouldBe(BookingErrors.NotFound);
        (await RunAsync<QuickBookingService, Result<QuickBookingResult>>(host, staff, s => s.CreateAsync(Quick(second, theirDriver, InDays(40), InDays(42)), Ct)))
            .Error.ShouldBe(BookingErrors.Forbidden, "a member of an agency books for its agency only");

        // The agency of the file reads it, unchanged.
        var own = TestActors.Staff(second.AgencyId, Roles.Manager);
        var file = HandoverWorld.Ok(await RunAsync<BookingAdministrationQueries, Result<BookingFile>>(host, own, q => q.GetFileAsync(theirs.BookingId, Ct)));
        file.Status.ShouldBe(BookingStatus.Confirmed);
        file.AssignedVehicle.ShouldBeNull();
        HandoverWorld.Ok(await RunAsync<BookingAdministrationQueries, Result<QuoteView>>(host, own, q => q.GetQuoteVersionAsync(theirs.BookingId, 1, Ct)));
    }

    [Fact]
    public async Task The_hand_overs_of_a_file_are_seen_and_changed_by_its_agency_only()
    {
        var file = await HandoverWorld.ConfirmedAsync(host, Ct);
        var inspectionId = await HandoverWorld.InspectDepartureAsync(host, FleetData.Actor(Roles.Agent, $"agent-{Guid.NewGuid():N}@tests.local"), file, Ct);
        var other = await CreateAsync(host, Ct);
        Guid photoId;
        await using (var scope = host.CreateScope())
        {
            photoId = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Inspection>()
                .Where(i => i.Id == inspectionId)
                .SelectMany(i => i.Photos)
                .Select(p => p.Id)
                .FirstAsync(Ct);
        }

        var today = TunisTime.DateOf(DateTimeOffset.UtcNow.AddMinutes(-10));
        var stranger = TestActors.Staff(other.AgencyId, Roles.Manager);
        var seen = HandoverWorld.Ok(await RunAsync<HandoverQueries, Result<HandoverSummary>>(host, stranger, q => q.GetSummaryAsync(file.BookingId, Ct)));
        seen.Departure.ShouldBeNull();
        seen.Contracts.ShouldBeEmpty();
        (await RunAsync<HandoverQueries, Result<HandoverMediaFile>>(host, stranger, q => q.FindPhotoAsync(photoId, false, Ct))).Error.ShouldBe(HandoverErrors.PhotoNotFound);
        HandoverWorld.Ok(await RunAsync<HandoverQueries, Result<HandoverDay>>(host, stranger, q => q.GetDayAsync(file.Agency.AgencyId, today, Ct))).Departures.ShouldBeEmpty();
        (await RunAsync<DepartureService, Result<DepartureContext>>(host, stranger, s => s.GetContextAsync(file.BookingId, Ct))).IsFailure.ShouldBeTrue();
        (await RunAsync<HandoverInspectionService, Result>(host, stranger, s => s.RecordReadingsAsync(inspectionId, HandoverWorld.Readings(99_000, 1), Ct))).IsFailure.ShouldBeTrue();

        var own = TestActors.Staff(file.Agency.AgencyId, Roles.Manager);
        var summary = HandoverWorld.Ok(await RunAsync<HandoverQueries, Result<HandoverSummary>>(host, own, q => q.GetSummaryAsync(file.BookingId, Ct)));
        summary.Departure.ShouldNotBeNull().Mileage.ShouldBe(15_100, "the readings of the agency stay");
        HandoverWorld.Ok(await RunAsync<HandoverQueries, Result<HandoverMediaFile>>(host, own, q => q.FindPhotoAsync(photoId, false, Ct)));
        HandoverWorld.Ok(await RunAsync<HandoverQueries, Result<HandoverDay>>(host, own, q => q.GetDayAsync(file.Agency.AgencyId, today, Ct)))
            .Departures.ShouldContain(d => d.BookingId == file.BookingId);
    }

    [Fact]
    public async Task A_channel_reference_is_unique_within_an_agency_only()
    {
        var (first, second) = (await CreateAsync(host, Ct), await CreateAsync(host, Ct));
        var reference = $"BDC-ISO-{Guid.NewGuid():N}"[..24];

        var inFirst = await ChannelBookingAsync(first, reference);
        var inSecond = await ChannelBookingAsync(second, reference);
        var replayed = await ChannelBookingAsync(first, reference);

        inSecond.Outcome.ShouldBe(ChannelBookingOutcome.Created, "the same reference in another agency is another booking");
        inSecond.BookingId.ShouldNotBe(inFirst.BookingId);
        inSecond.AgencyId.ShouldBe(second.AgencyId);
        replayed.Outcome.ShouldBe(ChannelBookingOutcome.AlreadyExisted);
        replayed.BookingId.ShouldBe(inFirst.BookingId);
        await using var scope = host.CreateScope(TestActors.Staff(first.AgencyId, Roles.Manager));
        var channels = scope.ServiceProvider.GetRequiredService<IChannelBookings>();
        (await channels.FindByChannelReferenceAsync(first.AgencyId, SalesChannel.BookingCom, reference, Ct)).ShouldNotBeNull().Id.ShouldBe(inFirst.BookingId);
        (await channels.FindByChannelReferenceAsync(second.AgencyId, SalesChannel.BookingCom, reference, Ct)).ShouldBeNull("never the booking of another agency");
        var queries = scope.ServiceProvider.GetRequiredService<IBookingsQueries>();
        var driver = (await queries.GetSummaryAsync(inFirst.BookingId, Ct)).ShouldNotBeNull().MainDriverId;
        await using var check = host.CreateScope();
        (await check.ServiceProvider.GetRequiredService<IBookingsQueries>().GetSummaryAsync(inSecond.BookingId, Ct)).ShouldNotBeNull()
            .MainDriverId.ShouldNotBe(driver, "the driver of each booking is a customer of its agency");
    }

    [Fact]
    public async Task My_booking_on_the_site_of_an_agency_never_opens_a_file_of_another_agency_even_with_its_number_and_e_mail()
    {
        var (first, second) = (await CreateAsync(host, Ct), await CreateAsync(host, Ct));
        var ours = await FunnelWorld.SubmitAsync(host, first, InDays(60), InDays(63), Ct);
        var theirs = await FunnelWorld.SubmitAsync(host, second, InDays(60), InDays(63), Ct);
        theirs.Number.ShouldBe(ours.Number, "each agency numbers its own files");

        var onFirstSite = await RequestAccessAsync(first.AgencyId, theirs.Email, theirs.Number);
        var onSecondSite = await RequestAccessAsync(second.AgencyId, theirs.Email, theirs.Number);

        await using (var scope = host.CreateScope())
        {
            var grants = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<BookingAccessGrant>().AsNoTracking();
            var refused = await grants.SingleAsync(g => g.Id == onFirstSite, Ct);
            refused.BookingId.ShouldBeNull("the number and the e-mail designate a file of another agency: nothing is found, nothing is sent");
            refused.AgencyId.ShouldBe(first.AgencyId);
            var granted = await grants.SingleAsync(g => g.Id == onSecondSite, Ct);
            granted.BookingId.ShouldBe(theirs.BookingId);
            granted.AgencyId.ShouldBe(second.AgencyId);
        }

        // The right code of the request opens it on the site of its agency only; the e-mail comes from that agency.
        var agency = (await host.Factory.Services.GetRequiredService<CarRental.Application.Referential.Contracts.IAgencyDirectory>().FindAsync(second.AgencyId, Ct)).ShouldNotBeNull();
        var email = await TestEmails.WaitForAsync(
            host.Factory.EmailDirectory,
            e => e.To.Contains(theirs.Email, StringComparison.Ordinal) && e.Subject == $"{agency.Name} — Votre accès à la réservation {theirs.Number}",
            Ct);
        TestEmails.Links(email).ShouldContain(l => l.Contains($"/{agency.Slug}/fr/ma-reservation/acces/", StringComparison.Ordinal), "the link leads to the site of the agency of the booking");
        var code = TestEmails.Code(email).ShouldNotBeNull();
        (await MyBookingAsync(first.AgencyId, s => s.OpenWithCodeAsync(onSecondSite, code, Ct))).Error.ShouldBe(AccessErrors.AccessInvalid);
        var opened = await MyBookingAsync(second.AgencyId, s => s.OpenWithCodeAsync(onSecondSite, code, Ct));
        opened.IsSuccess.ShouldBeTrue(opened.IsFailure ? opened.Error.ToString() : null);
    }

    [Fact]
    public async Task A_funnel_session_of_the_site_of_an_agency_is_unknown_on_the_site_of_another_agency()
    {
        var (first, second) = (await CreateAsync(host, Ct), await CreateAsync(host, Ct));
        string token;
        await using (var scope = host.CreateScope(SiteOf(first.AgencyId, "system:public-funnel")))
        {
            var started = await scope.ServiceProvider.GetRequiredService<FunnelService>().StartAsync(
                new FunnelStartRequest(first.AgencyId, "fr", SalesChannel.Web, null, first.Ecmr, "ECMR", first.OfficeId, first.OfficeId, InDays(50), InDays(53), "?lieu=X", null),
                Ct);
            token = HandoverWorld.Ok(started).Token;
        }

        var drivers = new FunnelDriversInput(FunnelWorld.Driver(Visitor.Unique()), false, null, true, false, false);
        await using (var scope = host.CreateScope(SiteOf(second.AgencyId, "system:public-funnel")))
        {
            var funnel = scope.ServiceProvider.GetRequiredService<FunnelService>();
            (await funnel.SaveDriversAsync(token, drivers, Ct)).Error.ShouldBe(FunnelErrors.SessionNotFound);
            (await funnel.SubmitAsync(token, termsAccepted: true, "http://localhost/", "fr", Ct)).Error.ShouldBe(FunnelErrors.SessionNotFound);
        }

        await using (var scope = host.CreateScope(SiteOf(first.AgencyId, "system:public-funnel")))
        {
            var saved = HandoverWorld.Ok(await scope.ServiceProvider.GetRequiredService<FunnelService>().SaveDriversAsync(token, drivers, Ct));
            saved.IsValid.ShouldBeTrue("the session goes on on the site it was opened on");
        }
    }

    /// <summary>The actor of a public page of the site of <paramref name="agencyId"/> (the system for that agency only).</summary>
    private static UserContextSnapshot SiteOf(Guid agencyId, string actor) => UserContextSnapshot.System(actor, agencyId: agencyId);

    private async Task<Guid> RequestAccessAsync(Guid siteAgencyId, string email, string number)
    {
        var outcome = await MyBookingAsync(siteAgencyId, s => s.RequestAccessAsync(siteAgencyId, email, number, "fr", "http://localhost/", Ct));
        outcome.Problems.ShouldBeEmpty();
        return outcome.RequestId.ShouldNotBeNull();
    }

    private async Task<T> MyBookingAsync<T>(Guid siteAgencyId, Func<MyBookingService, Task<T>> call)
    {
        await using var scope = host.CreateScope(SiteOf(siteAgencyId, "system:my-booking"));
        return await call(scope.ServiceProvider.GetRequiredService<MyBookingService>());
    }

    private async Task<ChannelBookingResult> ChannelBookingAsync(BookingAgency agency, string reference)
    {
        await using var scope = host.CreateScope(TestActors.SystemFor(agency.AgencyId, "system:channels"));
        var created = await scope.ServiceProvider.GetRequiredService<IChannelBookings>().CreateConfirmedAsync(
            new ChannelBookingRequest(
                agency.AgencyId,
                SalesChannel.BookingCom,
                reference,
                agency.Ecmr,
                agency.OfficeId,
                agency.OfficeId,
                InDays(45),
                InDays(47),
                new ChannelDriverInfo("Jonas", "Weber", "+4917012345678"),
                IgnoreChannelRestrictions: true),
            cancellationToken: Ct);
        return HandoverWorld.Ok(created);
    }
}
