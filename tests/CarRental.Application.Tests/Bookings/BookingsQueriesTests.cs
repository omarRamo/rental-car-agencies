using CarRental.Application.Bookings.Administration;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Queries;
using CarRental.Application.Common.Paging;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Bookings.Fakes;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Access;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Bookings;

/// <summary>The read side: contract queries (persons, requests, agenda, late returns, shortfalls) and the back office views.</summary>
public sealed class BookingsQueriesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_person_with_an_open_file_keeps_it_until_the_file_is_closed_or_cancelled()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync(harness.Quick() with { SecondaryDriverId = harness.SecondDriverId });
        var stranger = harness.Customers.AddPerson("Nour", "Jaziri");

        (await harness.Queries.HasOpenBookingsAsync(harness.MainDriverId, Ct)).ShouldBeTrue();
        (await harness.Queries.HasOpenBookingsAsync(harness.SecondDriverId, Ct)).ShouldBeTrue();
        (await harness.Queries.HasOpenBookingsAsync(stranger, Ct)).ShouldBeFalse();

        (await harness.Workflow.CancelAsync(booking.Id, "Annulation", cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        (await harness.Queries.HasOpenBookingsAsync(harness.MainDriverId, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_files_of_a_person_carry_the_roles_the_person_holds()
    {
        var harness = new BookingsHarness();
        var own = await harness.ConfirmedAsync();
        var asSecond = await harness.ConfirmedAsync(harness.Quick() with
        {
            MainDriverId = harness.SecondDriverId,
            SecondaryDriverId = harness.MainDriverId,
            PickupAt = harness.PickupAt.AddDays(10),
            ReturnAt = harness.ReturnAt.AddDays(10),
        });

        var files = await harness.Queries.GetBookingsOfPersonAsync(harness.MainDriverId, Ct);

        files.Select(f => f.Booking.Id).ShouldBe([asSecond.Id, own.Id], "latest pick-up first");
        files[0].Roles.ShouldBe([BookingPartyRoleInfo.SecondaryDriver]);
        files[1].Roles.ShouldBe([BookingPartyRoleInfo.MainDriver, BookingPartyRoleInfo.Payer]);
        files[1].CategoryCode.ShouldBe("ECMR");
        (await harness.Queries.FindByNumberAsync(harness.AgencyId, " loc-2027-000001 ", Ct)).ShouldNotBeNull().Id.ShouldBe(own.Id);
        (await harness.Queries.FindByNumberAsync(null, "LOC-2027-000001", Ct)).ShouldNotBeNull("in the agency of the operation").Id.ShouldBe(own.Id);
        (await harness.Queries.FindByNumberAsync(Guid.CreateVersion7(), "LOC-2027-000001", Ct)).ShouldBeNull("a number is unique within an agency only");
        (await harness.Queries.GetSummaryAsync(own.Id, Ct)).ShouldNotBeNull().Status.ShouldBe(BookingStatusInfo.Confirmed);
    }

    [Fact]
    public async Task Pending_requests_come_closest_hold_first_with_their_escalation()
    {
        var harness = new BookingsHarness();
        var first = await harness.PendingAsync();
        harness.Clock.Advance(TimeSpan.FromHours(1));
        var second = await harness.PendingAsync();

        var requests = await harness.Queries.GetPendingRequestsAsync(harness.AgencyId, Ct);

        requests.Select(r => r.BookingId).ShouldBe([first.Id, second.Id]);
        requests[0].CustomerName.ShouldBe("Sami Trabelsi");
        requests[0].TimeLeft(harness.Clock.GetUtcNow()).ShouldBe(TimeSpan.FromHours(23));
        requests[0].IsEscalated.ShouldBeFalse();
        requests[0].CategoryCode.ShouldBe("ECMR");
    }

    [Fact]
    public async Task The_agenda_lists_departures_and_returns_of_the_day_with_the_documents_state()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync();
        (await harness.Assignments.AssignAsync(booking.Id, harness.VehicleA.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        harness.Clock.SetUtcNow(harness.PickupAt.AddHours(1));

        var departureDay = await harness.Queries.GetDayAgendaAsync(harness.AgencyId, TunisTime.DateOf(harness.PickupAt), Ct);

        var departure = departureDay.Departures.ShouldHaveSingleItem();
        departure.PlateNumber.ShouldBe(harness.VehicleA.PlateNumber);
        departure.CustomerName.ShouldBe("Sami Trabelsi");
        departure.Documents.ShouldBe(DocumentsStateInfo.Complete);
        departure.IsLate.ShouldBeTrue("the planned pick-up passed and the vehicle did not leave");
        departure.IsDone.ShouldBeFalse();
        departureDay.Returns.ShouldBeEmpty("not in progress yet");
    }

    [Fact]
    public async Task Late_returns_and_unassigned_departures_are_reported()
    {
        var harness = new BookingsHarness();
        var late = await harness.ConfirmedAsync();
        (await harness.Assignments.AssignAsync(late.Id, harness.VehicleA.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        var unassigned = await harness.ConfirmedAsync(harness.Quick() with { PickupAt = harness.ReturnAt.AddDays(1), ReturnAt = harness.ReturnAt.AddDays(3) });
        harness.As(Roles.Preparer);
        harness.Clock.SetUtcNow(harness.PickupAt);
        (await harness.Lifecycle.StartRentalAsync(late.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        harness.Clock.SetUtcNow(harness.ReturnAt.AddHours(2));

        var lateReturns = await harness.Queries.GetLateReturnsAsync(harness.AgencyId, Ct);
        var soon = await harness.Queries.GetUnassignedStartingWithinAsync(harness.AgencyId, TimeSpan.FromHours(48), Ct);

        lateReturns.ShouldHaveSingleItem().BookingId.ShouldBe(late.Id);
        lateReturns[0].Phone.ShouldBe("+21620111001");
        soon.ShouldHaveSingleItem().Id.ShouldBe(unassigned.Id);
    }

    [Fact]
    public void Consecutive_segments_over_capacity_merge_into_one_shortfall()
    {
        var harness = new BookingsHarness();
        var t0 = harness.PickupAt;
        Period P(int from, int to) => Period.Create(t0.AddHours(from), t0.AddHours(to)).Value;

        var shortfalls = BookingsQueries.Shortfalls(harness.Ecmr,
        [
            new CapacitySegmentInfo(P(0, 2), 2, 1),
            new CapacitySegmentInfo(P(2, 4), 2, 3),
            new CapacitySegmentInfo(P(4, 6), 1, 3),
            new CapacitySegmentInfo(P(6, 8), 2, 2),
            new CapacitySegmentInfo(P(8, 9), 0, 1),
        ]);

        shortfalls.Select(s => (s.Period, s.Missing)).ShouldBe([(P(2, 6), 2), (P(8, 9), 1)]);
        shortfalls.ShouldAllBe(s => s.CategoryCode == "ECMR");
    }

    [Fact]
    public async Task The_list_filters_by_status_and_searches_names_and_numbers()
    {
        var harness = new BookingsHarness();
        var confirmed = await harness.ConfirmedAsync();
        var pending = await harness.PendingAsync();
        var other = harness.Customers.AddPerson("Karim", "Mansouri");
        var mansouri = await harness.ConfirmedAsync(harness.Quick() with { MainDriverId = other });

        var all = await harness.Administration.ListAsync(new BookingListFilter(harness.AgencyId), new PageRequest(), Ct);
        all.Value.TotalCount.ShouldBe(3);
        (await harness.Administration.ListAsync(new BookingListFilter(Statuses: [BookingStatus.PendingValidation]), new PageRequest(), Ct))
            .Value.Items.ShouldHaveSingleItem().Id.ShouldBe(pending.Id);
        (await harness.Administration.ListAsync(new BookingListFilter(Search: "mansouri"), new PageRequest(), Ct))
            .Value.Items.ShouldHaveSingleItem().Id.ShouldBe(mansouri.Id);
        (await harness.Administration.ListAsync(new BookingListFilter(Search: "000001"), new PageRequest(), Ct))
            .Value.Items.ShouldHaveSingleItem().Id.ShouldBe(confirmed.Id);
        harness.As(Roles.Preparer);
        (await harness.Administration.ListAsync(new BookingListFilter(), new PageRequest(), Ct)).IsSuccess.ShouldBeTrue("the preparer reads the files");
    }

    [Fact]
    public async Task The_file_shows_its_quote_versions_timeline_and_the_actions_of_the_user()
    {
        var harness = new BookingsHarness();
        var booking = await harness.PendingAsync();

        var file = (await harness.Administration.GetFileAsync(booking.Id, Ct)).Value;

        file.Number.ShouldBe(booking.Number);
        file.Timeline.Select(t => t.Type).ShouldBe([BookingEventType.Submitted, BookingEventType.Created], "latest first");
        file.QuoteVersions.ShouldHaveSingleItem().IsCurrent.ShouldBeTrue();
        file.CurrentQuote.ShouldNotBeNull().Lines.Count.ShouldBe(2);
        file.Drivers.ShouldHaveSingleItem().Eligibility.ShouldNotBeNull().IsEligible.ShouldBeTrue();
        file.Actions.CanValidate.ShouldBeTrue();
        file.Actions.CanStartRental.ShouldBeFalse();
        file.Actions.CanDiscount.ShouldBeFalse("an agent has no discount permission by default");

        harness.As(Roles.Accountant);
        var readOnly = (await harness.Administration.GetFileAsync(booking.Id, Ct)).Value;
        readOnly.Actions.Any.ShouldBeFalse();
    }

    [Fact]
    public async Task The_checklist_flags_what_the_agent_must_look_at()
    {
        var harness = new BookingsHarness();
        var booking = await harness.PendingAsync();
        harness.Customers.Issues[harness.MainDriverId] = [new EligibilityIssueInfo(EligibilityIssueKindInfo.UnderMinimumAge, null, null, 21, 19)];
        harness.Customers.Blacklisted.Add(harness.MainDriverId);

        var checklist = (await harness.Administration.GetChecklistAsync(booking.Id, Ct)).Value;

        checklist.Items.Select(i => i.Kind).ShouldBe(Enum.GetValues<ChecklistItemKind>());
        checklist.Items.Single(i => i.Kind == ChecklistItemKind.AgeAndLicense).State.ShouldBe(ChecklistState.Attention);
        checklist.Items.Single(i => i.Kind == ChecklistItemKind.AgeAndLicense).Details.ShouldHaveSingleItem().Issue!.Kind.ShouldBe(EligibilityIssueKindInfo.UnderMinimumAge);
        checklist.Items.Single(i => i.Kind == ChecklistItemKind.CustomerCheck).State.ShouldBe(ChecklistState.Attention);
        checklist.Items.Single(i => i.Kind == ChecklistItemKind.CustomerCheck).Details.ShouldBeEmpty("never more specific than a generic check");
        checklist.Items.Single(i => i.Kind == ChecklistItemKind.Names).State.ShouldBe(ChecklistState.Manual);
        var payment = checklist.Items.Single(i => i.Kind == ChecklistItemKind.Payment);
        payment.State.ShouldBe(ChecklistState.Attention, "no online payment in the capture mode: the validation will send a payment link");
        payment.Payment!.State.ShouldBe(RequestPaymentState.None);
        payment.Payment.Mode.ShouldBe(BookingValidationPaymentMode.CaptureAtValidation);
        checklist.Items.Single(i => i.Kind == ChecklistItemKind.Availability).State.ShouldBe(ChecklistState.Ok);
        checklist.AllOk.ShouldBeFalse();
    }

    [Fact]
    public async Task The_live_table_prices_every_category_with_its_availability()
    {
        var harness = new BookingsHarness();
        harness.Pricing.DailyPrices[harness.Cdmr.Id] = 150m;
        harness.Availability.ByCategory[harness.Cdmr.Id] = new CategoryAvailabilityInfo(harness.Cdmr.Id, false, 0, null);

        var rows = await harness.Administration.QuoteCategoriesAsync(
            new QuickQuoteRequest(harness.AgencyId, SalesChannel.Phone, harness.OfficeId, harness.OfficeId, harness.PickupAt, harness.ReturnAt, DriverIds: [harness.MainDriverId]),
            Ct);

        rows.Value.Select(r => r.Category.AcrissCode).ShouldBe(["ECMR", "CDMR"]);
        rows.Value[0].TotalInclVat.ShouldBe(Money.Tnd(307.140m));
        rows.Value[0].IsAvailable.ShouldBeTrue();
        rows.Value[1].TotalInclVat.ShouldBe(Money.Tnd(457.140m));
        rows.Value[1].IsAvailable.ShouldBeFalse();
        harness.Pricing.Requests.ShouldAllBe(r => r.DriverBirthDates!.SequenceEqual(new[] { new DateOnly(1988, 4, 12) }));
    }
}
