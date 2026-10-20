using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Bookings.Fakes;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Bookings;

/// <summary>The public funnel contract: locked quotes, drafts and submitted web requests.</summary>
public sealed class BookingRequestServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PriceRequest Price(BookingsHarness harness, SalesChannel channel = SalesChannel.Web) =>
        new(harness.AgencyId, harness.Ecmr.Id, harness.OfficeId, harness.OfficeId, harness.PickupAt, harness.ReturnAt, channel, [new SelectedExtra("CHILD_SEAT", 1)]);

    [Fact]
    public async Task A_locked_quote_has_a_public_code_and_guarantees_its_price_for_24_hours()
    {
        var harness = new BookingsHarness();
        harness.AsSystem("system:whatsapp");

        var locked = await harness.Requests.CreateLockedQuoteAsync(Price(harness, SalesChannel.WhatsApp), Ct);

        locked.IsSuccess.ShouldBeTrue(locked.IsFailure ? locked.Error.ToString() : null);
        QuoteCode.IsValid(locked.Value.Code).ShouldBeTrue();
        locked.Value.ExpiresAtUtc.ShouldBe(BookingsHarness.Start.AddHours(24));
        locked.Value.IsExpired.ShouldBeFalse();
        locked.Value.Extras.ShouldHaveSingleItem().ShouldBe(new SelectedExtra("CHILD_SEAT", 1));
        harness.Db.Rows<Quote>().ShouldHaveSingleItem().Status.ShouldBe(QuoteStatus.Locked);
        harness.Guard.AvailabilityChecks.ShouldBeEmpty("a locked quote guarantees a price, not a vehicle");

        var found = await harness.Requests.GetLockedQuoteAsync(locked.Value.Code.ToLowerInvariant(), Ct);
        found.ShouldNotBeNull().TotalInclVat.ShouldBe(locked.Value.TotalInclVat);
        harness.Clock.Advance(TimeSpan.FromHours(24));
        (await harness.Requests.GetLockedQuoteAsync(locked.Value.Code, Ct)).ShouldNotBeNull().IsExpired.ShouldBeTrue();
        (await harness.Requests.GetLockedQuoteAsync("NOPE00", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_submitted_request_holds_capacity_with_its_payer_and_consent()
    {
        var harness = new BookingsHarness();
        harness.AsSystem("system:public-funnel");

        var submitted = await harness.Requests.SubmitRequestAsync(
            new BookingRequestDraft(SalesChannel.Web, Price: Price(harness), Attribution: new RequestAttributionInfo("google", "google", "cpc", "ete2027")),
            new BookingRequestParties(harness.MainDriverId, harness.SecondDriverId, WhatsAppNotificationsConsent: true),
            Ct);

        submitted.IsSuccess.ShouldBeTrue(submitted.IsFailure ? submitted.Error.ToString() : null);
        submitted.Value.Status.ShouldBe(BookingStatusInfo.PendingValidation);
        submitted.Value.HoldExpiresAt.ShouldBe(BookingsHarness.Start.AddHours(24));
        var booking = harness.Db.Rows<Booking>().ShouldHaveSingleItem();
        booking.Channel.ShouldBe(SalesChannel.Web);
        booking.Attribution!.UtmCampaign.ShouldBe("ete2027");
        booking.PayerCustomerId.ShouldBe(harness.Customers.IndividualCustomers[harness.MainDriverId]);
        booking.SecondaryDriverId.ShouldBe(harness.SecondDriverId);
        booking.Extras.ShouldHaveSingleItem().Code.ShouldBe("CHILD_SEAT");
        harness.Customers.Consents.ShouldHaveSingleItem().ShouldBe((harness.MainDriverId, ConsentPurposeInfo.WhatsAppNotifications, ConsentChannelInfo.Web, true));
        harness.Guard.AvailabilityChecks.ShouldHaveSingleItem().Request.Booking.Kind.ShouldBe(OccupancyKind.Planned);
        harness.Customers.BlacklistChecks.ShouldBeEmpty("nothing hints the customer check to an applicant");
        booking.Events.Select(e => e.Type).ShouldBe([BookingEventType.Created, BookingEventType.Submitted]);
        booking.Events.ShouldAllBe(e => e.Channel == SalesChannel.Web);
        harness.Outbox.Messages.OfType<BookingStatusChanged>().Select(m => m.Status).ShouldBe([BookingStatusInfo.Draft, BookingStatusInfo.PendingValidation]);
        harness.Notifier.Changed.ShouldBe([harness.Ecmr.Id]);
    }

    [Fact]
    public async Task The_hold_comes_from_the_settings_of_the_agency()
    {
        var harness = new BookingsHarness();
        harness.Settings.Set(WellKnownSettings.BookingHoldDuration, TimeSpan.FromHours(6));

        var booking = await harness.PendingAsync();

        booking.HoldExpiresAt.ShouldBe(BookingsHarness.Start.AddHours(6));
    }

    [Fact]
    public async Task A_request_from_a_locked_quote_keeps_its_guaranteed_price()
    {
        var harness = new BookingsHarness();
        var locked = (await harness.Requests.CreateLockedQuoteAsync(Price(harness), Ct)).Value;
        harness.Pricing.DailyPrices[harness.Ecmr.Id] = 150m;
        harness.Pricing.Requests.Clear();

        var submitted = await harness.Requests.SubmitRequestAsync(
            new BookingRequestDraft(SalesChannel.WhatsApp, LockedQuoteCode: locked.Code),
            new BookingRequestParties(harness.MainDriverId),
            Ct);

        submitted.IsSuccess.ShouldBeTrue(submitted.IsFailure ? submitted.Error.ToString() : null);
        submitted.Value.TotalInclVat.ShouldBe(locked.TotalInclVat);
        harness.Pricing.Requests.ShouldBeEmpty("the locked price is copied, not priced again");
        var version = harness.Db.Rows<Quote>().Single(q => q.Status == QuoteStatus.BookingVersion);
        version.Origin.ShouldBe(QuoteOrigin.LockedQuote);
        version.SourceQuoteId.ShouldBe(locked.QuoteId);
        version.Code.ShouldNotBe(locked.Code);
        var booking = harness.Db.Rows<Booking>().Single();
        booking.Channel.ShouldBe(SalesChannel.WhatsApp);
        booking.Extras.ShouldHaveSingleItem().Code.ShouldBe("CHILD_SEAT");
    }

    [Fact]
    public async Task An_expired_or_unknown_locked_quote_asks_for_a_new_price()
    {
        var harness = new BookingsHarness();
        var locked = (await harness.Requests.CreateLockedQuoteAsync(Price(harness), Ct)).Value;
        harness.Clock.Advance(TimeSpan.FromHours(25));

        (await harness.Requests.SubmitRequestAsync(new BookingRequestDraft(SalesChannel.Web, LockedQuoteCode: locked.Code), new BookingRequestParties(harness.MainDriverId), Ct))
            .Error.ShouldBe(BookingErrors.QuoteExpired);
        (await harness.Requests.CreateDraftAsync(new BookingRequestDraft(SalesChannel.Web, LockedQuoteCode: "Q7F3K2"), Ct)).Error.ShouldBe(BookingErrors.QuoteNotFound);
        (await harness.Requests.CreateDraftAsync(new BookingRequestDraft(SalesChannel.Web, LockedQuoteCode: "bad"), Ct)).Error.ShouldBe(BookingErrors.QuoteCodeInvalid);
    }

    [Fact]
    public async Task A_draft_occupies_nothing_until_it_is_submitted()
    {
        var harness = new BookingsHarness();

        var draft = await harness.Requests.CreateDraftAsync(new BookingRequestDraft(SalesChannel.Web, Price: Price(harness)), Ct);

        draft.Value.Status.ShouldBe(BookingStatusInfo.Draft);
        harness.Guard.AvailabilityChecks.ShouldBeEmpty();
        var booking = harness.Db.Rows<Booking>().Single();
        booking.MainDriverId.ShouldBeNull();

        var submitted = await harness.Requests.SubmitAsync(draft.Value.BookingId, new BookingRequestParties(harness.MainDriverId), Ct);
        submitted.Value.Status.ShouldBe(BookingStatusInfo.PendingValidation);
        harness.Guard.AvailabilityChecks.ShouldHaveSingleItem().Request.Booking.BookingId.ShouldBe(booking.Id);
        (await harness.Requests.SubmitAsync(Guid.CreateVersion7(), new BookingRequestParties(harness.MainDriverId), Ct)).Error.ShouldBe(BookingErrors.NotFound);
    }

    [Fact]
    public async Task A_request_that_no_longer_fits_is_not_submitted()
    {
        var harness = new BookingsHarness();
        harness.Guard.NextAvailability = AvailabilityErrors.NotAvailable;

        var submitted = await harness.Requests.SubmitRequestAsync(
            new BookingRequestDraft(SalesChannel.Web, Price: Price(harness)),
            new BookingRequestParties(harness.MainDriverId),
            Ct);

        submitted.Error.ShouldBe(AvailabilityErrors.NotAvailable);
        harness.Notifier.Changed.ShouldBeEmpty();
        harness.Journal.Entries.ShouldContain("rollback");
    }

    [Theory]
    [InlineData(SalesChannel.Phone)]
    [InlineData(SalesChannel.Counter)]
    [InlineData(SalesChannel.BookingCom)]
    public async Task Only_web_and_whatsapp_requests_go_through_the_funnel(SalesChannel channel)
    {
        var harness = new BookingsHarness();

        (await harness.Requests.CreateDraftAsync(new BookingRequestDraft(channel, Price: Price(harness)), Ct)).Error.ShouldBe(BookingErrors.ChannelInvalid);
        (await harness.Requests.CreateDraftAsync(new BookingRequestDraft(SalesChannel.Web), Ct)).Error.ShouldBe(BookingErrors.ChannelInvalid);
    }

    [Fact]
    public async Task The_customer_provides_the_information_and_the_request_is_checked_again()
    {
        var harness = new BookingsHarness();
        var booking = await harness.PendingAsync();
        (await harness.Workflow.RequestInformationAsync(booking.Id, InformationRequestReason.MissingDocuments, "Verso du permis manquant.", Ct)).IsSuccess.ShouldBeTrue();
        harness.Reset();
        harness.AsSystem("system:public-funnel");

        (await harness.Requests.ProvideInformationAsync(booking.Id, Ct)).IsSuccess.ShouldBeTrue();

        booking.Status.ShouldBe(BookingStatus.PendingValidation);
        harness.Guard.AvailabilityChecks.ShouldHaveSingleItem();
        booking.Events[^1].Channel.ShouldBe(SalesChannel.Web);
        booking.Events[^1].ActorName.ShouldBe("system:public-funnel");
    }
}
