using System.Globalization;
using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Administration;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Handover;
using CarRental.Application.Bookings.PublicAccess;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Channels.Administration;
using CarRental.Application.Common.Security;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Persistence;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using static CarRental.Integration.Tests.Scenarios.HighSeasonWorld;

namespace CarRental.Integration.Tests.Scenarios;

/// <summary>
/// « Une journée type de haute saison se joue de bout en bout sans surréservation » (PROMPT §15, phase 1), on a host and
/// a clock of its own (docs/scenarios/journee-haute-saison.md): the week before builds the planning of 15 July through
/// the real services (bookings by phone, the funnel, Booking.com; departures on the tablet), then the day is played hour
/// by hour — requests validated, refused, sent back for information; an option confirmed; walk-ins; a WhatsApp quote
/// turned into a request; a Booking.com arrival; a later return and an upgrade; a late return priced by the engine; a
/// no-show; cancellations — and an evening rush of 150 concurrent sales from every channel on the last vehicles. The
/// day ends with the invariants checked on the stored rows, the planning and the dashboard of the day.
/// </summary>
[Collection(HighSeasonCollection.Name)]
public sealed class HighSeasonDayTests(HighSeasonHost host)
{
    private HighSeasonWorld _w = null!;

    // The files of the day, named by their story.
    private Guid _onTimeReturn;
    private Guid _lateReturn;
    private Guid _extension;
    private Guid _nineOClockDeparture;
    private Guid _elevenOClockDeparture;
    private Guid _noShow;
    private Guid _upgrade;
    private Guid _cancelledWithFee;
    private Guid _cancelledOption;
    private Guid _expiringRequest;
    private Guid _validatedRequest;
    private Guid _refusedRequest;
    private Guid _incompleteRequest;
    private Guid _option;
    private Guid _whatsAppRequest;
    private Guid _bookingComArrival;
    private Guid _walkIn;
    private Guid _weekendBookingCom;
    private Guid _weekendPhone;
    private Guid _cashSession;
    private Money _walkInCash;
    private Money _walkInDeposit;
    private Money _lateCharges;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_high_season_day_plays_from_the_opening_to_the_closing_without_any_overbooking()
    {
        _w = await OpenAgencyAsync(host, NextHighSeasonDay(host.Clock.Today));

        await TheWeekBeforeAsync();
        await TheOpeningAsync();
        await TheMorningAsync();
        await TheMiddayAsync();
        await TheAfternoonAsync();
        var rush = await TheEveningRushAsync();
        await TheClosingAsync(rush);
    }

    /// <summary>D-6 to D-1: the bookings of the week, the departures of the rentals that come back on the day, the planning.</summary>
    private async Task TheWeekBeforeAsync()
    {
        _w.JumpTo(-6, 9);
        var driver = await _w.NewDriverAsync();
        _onTimeReturn = (await _w.BookAsync(_w.Quick(_w.Ecmr, driver.PersonId, _w.At(-5, 10), _w.At(0, 10)))).BookingId;
        await _w.RestrictBookingComAsync(ChannelQuotaKind.Reserve, _w.Ifmr, _w.Day.AddDays(-6), _w.Day.AddDays(20), 1, "Haute saison : un SUV gardé hors Booking.com");

        _w.JumpTo(-6, 9, 10);
        var lateEntry = _w.Extranet(_w.Cdmr, _w.At(-3, 9), _w.At(0, 9), gross: 600m);
        _lateReturn = (await _w.EnterBookingComAsync(lateEntry)).BookingId;

        _w.JumpTo(-5, 9, 50);
        await _w.AssignAsync(_onTimeReturn, _w.Ecmr[1]);
        _w.JumpTo(-5, 10);
        await _w.DepartAsync(_onTimeReturn, _w.PayByCardAsync);

        _w.JumpTo(-5, 11);
        _cancelledWithFee = (await _w.BookAsync(_w.Quick(_w.Ifmr, (await _w.NewDriverAsync()).PersonId, _w.At(2, 10), _w.At(3, 10)))).BookingId;

        // A web request with its guarantee held on the bank, validated (the guarantee is captured): it leaves on D-2.
        _w.JumpTo(-4, 10);
        _extension = (await _w.RequestOnlineAsync(_w.Ifmr, _w.At(-2, 10), _w.At(0, 18), guarantee: true)).BookingId;
        _w.JumpTo(-4, 10, 30);
        (await ValidateAsync(_extension)).ShouldBe(ValidationOutcome.Confirmed);

        _w.JumpTo(-4, 11);
        _nineOClockDeparture = (await _w.BookAsync(_w.Quick(_w.Ecmr, (await _w.NewDriverAsync()).PersonId, _w.At(0, 9), _w.At(3, 9)))).BookingId;

        // The Booking.com customer shows the originals at the counter; the rental was prepaid to the channel.
        _w.JumpTo(-3, 8, 45);
        await _w.CompleteChannelDriverAsync((await _w.FileAsync(_lateReturn)).MainDriverId!.Value);
        await _w.AssignAsync(_lateReturn, _w.Cdmr[1]);
        _w.JumpTo(-3, 9);
        await _w.DepartAsync(_lateReturn, async file =>
        {
            await _w.PayAsync(file.Id, PaymentMethodInfo.ChannelPrepaid, file.TotalInclVat.Amount, lateEntry.ChannelReference);
            await _w.TakeDepositAsync(file, DepositModeInfo.CardImprint);
        });

        _w.JumpTo(-3, 10);
        _elevenOClockDeparture = (await _w.BookAsync(_w.Quick(_w.Cdmr, (await _w.NewDriverAsync()).PersonId, _w.At(0, 11), _w.At(2, 11)))).BookingId;
        _w.JumpTo(-3, 10, 30);
        _noShow = (await _w.BookAsync(_w.Quick(_w.Ecmr, (await _w.NewDriverAsync()).PersonId, _w.At(0, 10), _w.At(2, 10)))).BookingId;

        // The web rental leaves: its guarantee covers the whole rental, only the deposit is taken.
        _w.JumpTo(-2, 9, 45);
        await _w.AssignAsync(_extension, _w.Ifmr[1]);
        _w.JumpTo(-2, 10);
        await _w.DepartAsync(_extension, async file =>
        {
            (await _w.PaidAsync(file.Id)).ShouldBe(file.TotalInclVat.Amount, "the pre-authorization was captured at the validation");
            await _w.TakeDepositAsync(file, DepositModeInfo.CardImprint);
        });

        _w.JumpTo(-2, 11);
        _upgrade = (await _w.BookAsync(_w.Quick(_w.Ecmr, (await _w.NewDriverAsync()).PersonId, _w.At(3, 10), _w.At(5, 10)))).BookingId;
        _w.JumpTo(-2, 12);
        _weekendBookingCom = (await _w.EnterBookingComAsync(_w.Extranet(_w.Ecmr, _w.At(7, 16), _w.At(13, 10), gross: 900m))).BookingId;

        _w.JumpTo(-1, 7, 30);
        _expiringRequest = (await _w.RequestOnlineAsync(_w.Cdmr, _w.At(5, 10), _w.At(8, 10))).BookingId;

        // The planning of the day is prepared the day before.
        _w.JumpTo(-1, 9);
        await _w.AssignAsync(_nineOClockDeparture, _w.Ecmr[2]);
        await _w.AssignAsync(_elevenOClockDeparture, _w.Cdmr[1]);
        await _w.AssignAsync(_noShow, _w.Ecmr[3]);

        _w.JumpTo(-1, 10);
        _cancelledOption = (await _w.BookAsync(_w.Quick(_w.Cdmr, (await _w.NewDriverAsync()).PersonId, _w.At(4, 10), _w.At(6, 10), QuickBookingOutcome.Option))).BookingId;
        _w.JumpTo(-1, 11);
        _weekendPhone = (await _w.BookAsync(_w.Quick(_w.Ecmr, (await _w.NewDriverAsync()).PersonId, _w.At(8, 9), _w.At(12, 18)))).BookingId;

        _w.JumpTo(-1, 22, 10);
        var validated = await _w.RequestOnlineAsync(_w.Ecmr, _w.At(1, 10), _w.At(4, 10), guarantee: true);
        _validatedRequest = validated.BookingId;
        validated.Guarantee.Mode.ShouldBe(FunnelGuaranteeMode.PreAuthorization);

        _w.JumpTo(0, 6, 50);
        _refusedRequest = (await _w.RequestOnlineAsync(_w.Cdmr, _w.At(6, 10), _w.At(9, 10))).BookingId;
        _w.JumpTo(0, 7, 45);
        _incompleteRequest = (await _w.RequestOnlineAsync(_w.Ifmr, _w.At(4, 10), _w.At(7, 10))).BookingId;

        (await _w.StatusAsync(_onTimeReturn)).ShouldBe(BookingStatusInfo.InProgress);
        (await _w.StatusAsync(_lateReturn)).ShouldBe(BookingStatusInfo.InProgress);
        (await _w.StatusAsync(_extension)).ShouldBe(BookingStatusInfo.InProgress);
    }

    /// <summary>08:00: the expiry job, the cash drawer and the dashboard at the opening.</summary>
    private async Task TheOpeningAsync()
    {
        _w.JumpTo(0, 8);
        await host.RunJobAsync("bookings.expire", Ct);
        (await _w.StatusAsync(_expiringRequest)).ShouldBe(BookingStatusInfo.Expired, "its 24-hour hold ended at 07:30 without an answer");
        (await _w.StatusAsync(_validatedRequest)).ShouldBe(BookingStatusInfo.PendingValidation, "its hold runs until 22:10");

        _cashSession = await _w.OkAsync<ICashSessions, Guid>(Agent, c => c.OpenAsync(new OpenCashSessionRequest(_w.AgencyId, 150m), Ct));

        var dashboard = await DashboardAsync();
        dashboard.Today.Departures.Select(d => d.BookingId).ShouldBe([_nineOClockDeparture, _noShow, _elevenOClockDeparture]);
        dashboard.Today.Returns.Select(r => r.BookingId).ShouldBe([_lateReturn, _onTimeReturn, _extension]);
        dashboard.Requests.ShouldNotBeNull().Select(r => r.BookingId).ShouldBe([_validatedRequest, _refusedRequest, _incompleteRequest], ignoreOrder: true);
        dashboard.LateReturns.ShouldBeEmpty("the first return is due at 09:00");
        dashboard.Shortfalls.ShouldBeEmpty();
    }

    /// <summary>08:15 to 11:05: the request queue, an option, the first departures and returns, WhatsApp, Booking.com, walk-ins.</summary>
    private async Task TheMorningAsync()
    {
        // The request queue: validated (its guarantee is captured), refused with a reason, sent back for a document.
        _w.JumpTo(0, 8, 15);
        (await ValidateAsync(_validatedRequest)).ShouldBe(ValidationOutcome.Confirmed);
        var validated = await _w.FileAsync(_validatedRequest);
        validated.TotalInclVat.ShouldBe(Money.Tnd(406.140m), "3 summer days at 140.000, 5 % off from 3 days, and the daily contribution");
        (await _w.PaidAsync(_validatedRequest)).ShouldBe(validated.TotalInclVat.Amount);

        _w.JumpTo(0, 8, 20);
        await _w.OkAsync<BookingWorkflowService>(Agent, s => s.RefuseAsync(_refusedRequest, "Doublon : le client a réservé par téléphone.", Ct));
        _w.JumpTo(0, 8, 25);
        await _w.OkAsync<BookingWorkflowService>(Agent, s => s.RequestInformationAsync(_incompleteRequest, InformationRequestReason.MissingDocuments, "Merci d'envoyer le verso de votre permis.", Ct));

        _w.JumpTo(0, 8, 30);
        var option = await _w.BookAsync(_w.Quick(_w.Ifmr, (await _w.NewDriverAsync()).PersonId, _w.At(1, 9), _w.At(4, 9), QuickBookingOutcome.Option));
        _option = option.BookingId;
        option.OptionExpiresAt.ShouldBe(_w.At(1, 9), "the 48 hours of an option stop at its pick-up");

        _w.JumpTo(0, 9, 5);
        await _w.DepartAsync(_nineOClockDeparture, _w.PayByCardAsync);

        // 09:30: the Booking.com rental due at 09:00 is not back.
        _w.JumpTo(0, 9, 30);
        (await DashboardAsync()).LateReturns.ShouldHaveSingleItem().BookingId.ShouldBe(_lateReturn);

        // 09:40: back on time, nothing to charge: the imprint is released and the file closed.
        _w.JumpTo(0, 9, 40);
        var (onTime, settled) = await _w.ReturnAsync(_onTimeReturn, kilometres: 450, fuelEighths: 8);
        onTime.LateDays.ShouldBe(0);
        onTime.Proposal.ShouldBeEmpty();
        settled.Balance.ShouldBe(Money.Zero);
        await _w.ReleaseDepositAndCloseAsync(_onTimeReturn);

        // 09:40: the WhatsApp agent locks a quote for 24 hours (creer_lien_devis); the customer opens its link at 10:00.
        var locked = await _w.OkAsync<IBookingRequests, LockedQuoteInfo>(
            Bot,
            r => r.CreateLockedQuoteAsync(new PriceRequest(_w.AgencyId, _w.Ecmr.Id, _w.OfficeId, _w.OfficeId, _w.At(4, 14), _w.At(7, 14), SalesChannel.WhatsApp), Ct));
        _w.JumpTo(0, 10);
        var whatsApp = await _w.RequestOnlineAsync(_w.Ecmr, _w.At(4, 14), _w.At(7, 14), channel: SalesChannel.WhatsApp, quoteCode: locked.Code);
        _whatsAppRequest = whatsApp.BookingId;
        var whatsAppFile = await _w.FileAsync(_whatsAppRequest);
        whatsAppFile.Channel.ShouldBe(SalesChannel.WhatsApp);
        whatsAppFile.TotalInclVat.ShouldBe(locked.TotalInclVat, "the price guaranteed by the bot");
        (await QuoteOf(_whatsAppRequest)).Origin.ShouldBe(QuoteOrigin.LockedQuote);

        // 10:15: the SUV in progress is kept one more day: the added day is priced at the summer plan.
        _w.JumpTo(0, 10, 15);
        var extended = await AmendAsync(_extension, file => file with { ReturnAt = _w.At(1, 18) });
        extended.QuoteVersion.ShouldBe(2);
        extended.TotalInclVat.ShouldBe(Money.Tnd(997.520m), "3 days (748.140) then 4: 4 × 260.000 − 5 % + 4 contributions");
        (await QuoteOf(_extension)).Origin.ShouldBe(QuoteOrigin.Extension);

        // 10:15: a reservation sold on the Booking.com extranet is entered: confirmed at once, at the engine's price.
        var arrival = await _w.EnterBookingComAsync(_w.Extranet(_w.Ecmr, _w.At(0, 14), _w.At(3, 14), gross: 480m, payment: ChannelPaymentMode.PayOnSite));
        _bookingComArrival = arrival.BookingId;
        (await _w.StatusAsync(_bookingComArrival)).ShouldBe(BookingStatusInfo.Confirmed);
        arrival.EngineTotalInclVat.ShouldBe(Money.Tnd(406.140m));

        _w.JumpTo(0, 10, 30);
        (await ValidateAsync(_whatsAppRequest)).ShouldBe(ValidationOutcome.AwaitingPayment, "no guarantee was held: the payment link mode");

        // 10:30: a walk-in at the counter, paid in cash with a cash deposit, leaves at 10:50.
        _walkIn = (await _w.BookAsync(_w.Quick(_w.Cdmr, (await _w.NewDriverAsync("Yosra")).PersonId, _w.At(0, 10, 45), _w.At(2, 10, 45), channel: SalesChannel.Counter))).BookingId;
        await _w.AssignAsync(_walkIn, _w.Cdmr[2]);
        _w.JumpTo(0, 10, 50);
        await _w.DepartAsync(_walkIn, async file =>
        {
            _walkInCash = file.TotalInclVat;
            _walkInDeposit = file.Deposit;
            await _w.PayAsync(file.Id, PaymentMethodInfo.Cash, file.TotalInclVat.Amount);
            await _w.TakeDepositAsync(file, DepositModeInfo.Cash);
        });

        // 10:55: the compact of 11:00 waits for the late Booking.com car: the planning shows the conflict, the agent swaps.
        _w.JumpTo(0, 10, 55);
        var plan = await _w.OkAsync<VehicleAssignmentService, AssignmentPlanView>(Agent, s => s.ProposeAsync(_w.AgencyId, Period.Create(_w.Clock.GetUtcNow(), _w.At(1, 0)).Value, Ct));
        var conflict = plan.Conflicts.ShouldHaveSingleItem();
        (conflict.BookingId, conflict.VehicleId, conflict.Reason).ShouldBe((_elevenOClockDeparture, (Guid?)_w.Cdmr[1], AvailabilityErrors.VehicleOverlap));
        (await _w.RunAsync<VehicleAssignmentService, Result>(Agent, s => s.AssignAsync(_elevenOClockDeparture, _w.Cdmr[1], cancellationToken: Ct)))
            .Error.ShouldBe(AvailabilityErrors.VehicleOverlap, "the overdue car keeps its vehicle busy, preparation included");
        await _w.AssignAsync(_elevenOClockDeparture, _w.Cdmr[3]);
        _w.JumpTo(0, 11, 5);
        await _w.DepartAsync(_elevenOClockDeparture, _w.PayByCardAsync);
    }

    /// <summary>11:30 to 14:05: the channel restrictions, the no-show, the late return, the upgrade, the Booking.com departure.</summary>
    private async Task TheMiddayAsync()
    {
        // The compacts are full today and tomorrow: stopped on Booking.com, never on the other channels; and two compacts
        // of the next weekend are kept off Booking.com.
        _w.JumpTo(0, 11, 30);
        await _w.RestrictBookingComAsync(ChannelQuotaKind.StopSell, _w.Cdmr, _w.Day, _w.Day.AddDays(2), 0, "Compactes complètes");
        await _w.RestrictBookingComAsync(ChannelQuotaKind.Reserve, _w.Cdmr, _w.Day.AddDays(8), _w.Day.AddDays(12), 2, "Week-end : deux compactes hors Booking.com");
        (await _w.RemainingAsync(_w.Cdmr, _w.At(1, 14), _w.At(2, 8), SalesChannel.BookingCom)).ShouldBe(0, "stop-sell");
        (await _w.RemainingAsync(_w.Cdmr, _w.At(1, 14), _w.At(2, 8), SalesChannel.Web)).ShouldBe(1, "the stop-sell of Booking.com never closes the website");
        (await _w.RemainingAsync(_w.Cdmr, _w.At(1, 14), _w.At(2, 8), SalesChannel.Phone)).ShouldBe(1);
        (await _w.RemainingAsync(_w.Cdmr, _w.At(9, 10), _w.At(10, 10), SalesChannel.BookingCom)).ShouldBe(1, "3 compacts, 2 kept for the other channels");
        (await _w.RemainingAsync(_w.Cdmr, _w.At(9, 10), _w.At(10, 10), SalesChannel.Web)).ShouldBe(3);
        (await _w.RemainingAsync(_w.Ifmr, _w.At(5, 10), _w.At(6, 10), SalesChannel.BookingCom)).ShouldBe(0, "one SUV is kept off Booking.com and the other is taken");
        (await _w.RemainingAsync(_w.Ifmr, _w.At(5, 10), _w.At(6, 10), SalesChannel.Web)).ShouldBe(1, "the SUV kept off Booking.com stays on sale on the website");

        // 12:05: two hours after its pick-up time, the economy car of 10:00 was not collected: its capacity comes back.
        _w.JumpTo(0, 12, 5);
        (await _w.RemainingAsync(_w.Ecmr, _w.At(0, 13), _w.At(1, 8), SalesChannel.Counter)).ShouldBe(1);
        await _w.OkAsync<BookingWorkflowService>(Agent, s => s.MarkNoShowAsync(_noShow, "Client injoignable, ne s'est pas présenté.", Ct));
        (await _w.StatusAsync(_noShow)).ShouldBe(BookingStatusInfo.NoShow);
        (await _w.RemainingAsync(_w.Ecmr, _w.At(0, 13), _w.At(1, 8), SalesChannel.Counter)).ShouldBe(2);

        // 12:30: the Booking.com compact comes back 3 h 30 late with a quarter of a tank missing: one day priced by the
        // engine at the summer plan (4 days − 3 days, tier included) and the refuel, paid in cash; the imprint is released.
        _w.JumpTo(0, 12, 30);
        var (late, returned) = await _w.ReturnAsync(_lateReturn, kilometres: 590, fuelEighths: 6);
        late.LateDays.ShouldBe(1);
        late.Proposal.Select(l => (l.Kind, l.InclVat)).ShouldBe(
        [
            (ReturnChargeKind.Fuel, Money.Tnd(30m)),
            (ReturnChargeKind.LateReturn, Money.Tnd(180.500m)),
            (ReturnChargeKind.LateContribution, Money.Tnd(2.380m)),
        ]);
        var engine = await LateDayByTheEngineAsync(_lateReturn, late.ActualReturnAt);
        (late.Proposal.Where(l => l.Kind is ReturnChargeKind.LateReturn or ReturnChargeKind.LateContribution).Select(l => l.InclVat).Sum()).ShouldBe(engine);
        _lateCharges = returned.ChargesTotal;
        returned.Balance.ShouldBe(Money.Tnd(212.880m), "the rental was prepaid to Booking.com: only the charges are due");
        await _w.PayAsync(_lateReturn, PaymentMethodInfo.Cash, returned.Balance.Amount);
        await _w.ReleaseDepositAndCloseAsync(_lateReturn);

        // 13:00: the economy booking of D+3 becomes a compact (both categories locked, priced again at the summer plan).
        _w.JumpTo(0, 13);
        var upgraded = await AmendAsync(_upgrade, file => file with { CategoryId = _w.Cdmr.Id });
        upgraded.TotalInclVat.ShouldBe(Money.Tnd(384.760m), "2 summer days at 190.000 and the contribution");
        (await _w.FileAsync(_upgrade)).CategoryId.ShouldBe(_w.Cdmr.Id);
        (await QuoteOf(_upgrade)).Origin.ShouldBe(QuoteOrigin.Requote);

        // 13:30 and 14:05: the Booking.com customer shows the documents, pays on site and leaves with the economy car back
        // at 09:40.
        _w.JumpTo(0, 13, 30);
        await _w.CompleteChannelDriverAsync((await _w.FileAsync(_bookingComArrival)).MainDriverId!.Value);
        await _w.AssignAsync(_bookingComArrival, _w.Ecmr[1]);
        _w.JumpTo(0, 14, 5);
        await _w.DepartAsync(_bookingComArrival, _w.PayByCardAsync);
    }

    /// <summary>14:30 to 16:00: a cancellation with its fee, a request completed then validated, the option confirmed.</summary>
    private async Task TheAfternoonAsync()
    {
        _w.JumpTo(0, 14, 30);
        var fee = await _w.OkAsync<BookingWorkflowService, Money>(Agent, s => s.CancelAsync(_cancelledWithFee, "Vol annulé, le client reporte son voyage.", cancellationToken: Ct));
        fee.ShouldBe(Money.Tnd(52.476m), "cancelled within 3 days of the pick-up: 20 % of 262.380");

        _w.JumpTo(0, 15);
        await _w.OkAsync<IBookingRequests>(Funnel, r => r.ProvideInformationAsync(_incompleteRequest, Ct));
        (await _w.StatusAsync(_incompleteRequest)).ShouldBe(BookingStatusInfo.PendingValidation);
        _w.JumpTo(0, 15, 20);
        (await ValidateAsync(_incompleteRequest)).ShouldBe(ValidationOutcome.AwaitingPayment);

        _w.JumpTo(0, 16);
        await _w.OkAsync<BookingWorkflowService>(Agent, s => s.ConfirmOptionAsync(_option, Ct));
        (await _w.StatusAsync(_option)).ShouldBe(BookingStatusInfo.Confirmed);
    }

    /// <summary>
    /// 16:30: the rush. Every channel tries at once for the last two economy cars of the next weekend (100 attempts, periods
    /// that all overlap), for the compacts of that weekend, two of which are kept off Booking.com (40 attempts), and for an
    /// SUV tomorrow, when both are taken (10 attempts).
    /// </summary>
    private async Task<IReadOnlyList<SaleOutcome>> TheEveningRushAsync()
    {
        _w.JumpTo(0, 16, 10);
        var attempts = new List<SaleAttempt>();
        attempts.AddRange(await AttemptsAsync(_w.Ecmr, (SaleSource.Web, 20), (SaleSource.Funnel, 20), (SaleSource.Phone, 20), (SaleSource.Counter, 20), (SaleSource.BookingComExtranet, 10), (SaleSource.BookingComConnected, 10)));
        attempts.AddRange(await AttemptsAsync(_w.Cdmr, (SaleSource.BookingComConnected, 20), (SaleSource.Web, 10), (SaleSource.Phone, 10)));
        attempts.AddRange(await AttemptsAsync(_w.Ifmr, (SaleSource.Web, 4), (SaleSource.Phone, 3), (SaleSource.Counter, 3)));
        attempts.Count.ShouldBe(150);
        (await _w.RemainingAsync(_w.Ecmr, _w.At(8, 21), _w.At(11, 10), SalesChannel.Web)).ShouldBe(2);
        (await _w.RemainingAsync(_w.Cdmr, _w.At(9, 10), _w.At(10, 10), SalesChannel.BookingCom)).ShouldBe(1);

        _w.JumpTo(0, 16, 30);
        var started = TimeProvider.System.GetTimestamp();
        var outcomes = await SalesBurst.RunAsync(attempts);
        var elapsed = TimeProvider.System.GetElapsedTime(started);

        var report = string.Join(Environment.NewLine, outcomes.GroupBy(o => o.ToString()).Select(g => $"{g.Count()} × {g.Key}"));
        TestContext.Current.TestOutputHelper?.WriteLine($"The rush: {attempts.Count} attempts in {elapsed.TotalMilliseconds:F0} ms{Environment.NewLine}{report}");
        outcomes.ShouldAllBe(o => o.Exception == null, report);
        outcomes.Where(o => !o.IsSold).ShouldAllBe(o => o.RefusalCode == AvailabilityErrors.NotAvailable.Code, report);
        outcomes.Count(o => o.IsSold && o.Attempt.Category == _w.Ecmr).ShouldBe(2, report);
        outcomes.Count(o => o.IsSold && o.Attempt.Category == _w.Cdmr).ShouldBe(3, report);
        outcomes.Count(o => o.IsSold && o.Attempt.Category == _w.Cdmr && o.Attempt.Source == SaleSource.BookingComConnected).ShouldBeLessThanOrEqualTo(1, "two compacts are kept off Booking.com");
        outcomes.Count(o => o.IsSold && o.Attempt.Category == _w.Ifmr).ShouldBe(0, report);
        (await _w.RemainingAsync(_w.Ecmr, _w.At(8, 21), _w.At(11, 10), SalesChannel.Web)).ShouldBe(0);
        (await _w.RemainingAsync(_w.Cdmr, _w.At(9, 10), _w.At(10, 10), SalesChannel.Web)).ShouldBe(0);
        return outcomes;
    }

    /// <summary>17:00 to 19:15: the last cancellation, the planning of the coming days, the cash drawer, the checks of the day.</summary>
    private async Task TheClosingAsync(IReadOnlyList<SaleOutcome> rush)
    {
        _w.JumpTo(0, 17);
        (await _w.OkAsync<BookingWorkflowService, Money>(Agent, s => s.CancelAsync(_cancelledOption, "Le client ne confirme pas l'option.", cancellationToken: Ct)))
            .ShouldBe(Money.Zero, "an option is cancelled for free");

        // 17:30: the agent asks for the proposals of the greedy assigner over the next two weeks and applies them all.
        _w.JumpTo(0, 17, 30);
        var plan = await _w.OkAsync<VehicleAssignmentService, AssignmentPlanView>(Agent, s => s.ProposeAsync(_w.AgencyId, Period.Create(_w.Clock.GetUtcNow(), _w.At(15, 0)).Value, Ct));
        plan.Unassignable.ShouldBeEmpty();
        plan.Conflicts.ShouldBeEmpty();
        var sold = rush.Where(o => o.IsSold).Select(o => o.Result!.Value).ToList();
        plan.Proposals.Select(p => p.BookingId).ShouldBe(
            [_validatedRequest, _option, _upgrade, _incompleteRequest, _whatsAppRequest, _weekendBookingCom, _weekendPhone, .. sold],
            ignoreOrder: true);
        var applied = await _w.OkAsync<VehicleAssignmentService, IReadOnlyList<AssignmentOutcome>>(Agent, s => s.ApplyAsync([.. plan.Proposals.Select(p => new AssignmentChoice(p.BookingId, p.VehicleId))], Ct));
        applied.ShouldAllBe(a => a.IsSuccess);

        // 19:00: the drawer holds the float, the walk-in's cash and deposit and the late charges; counted as expected.
        _w.JumpTo(0, 19);
        var drawer = await _w.OkAsync<ICashSessions, CashSessionDetails>(Agent, c => c.GetAsync(_cashSession, Ct));
        drawer.ExpectedRevenueCash.ShouldBe(Money.Tnd(150m) + _walkInCash + _lateCharges);
        drawer.ExpectedDepositCash.ShouldBe(_walkInDeposit);
        (await _w.OkAsync<ICashSessions, CashSessionClosing>(Agent, c => c.CloseAsync(_cashSession, new CloseCashSessionRequest(drawer.ExpectedRevenueCash.Amount, drawer.ExpectedDepositCash.Amount), Ct)))
            .ShouldBe(new CashSessionClosing(Money.Zero, Money.Zero));
        await _w.OkAsync<ICashSessions>(Manager, c => c.LockAsync(_cashSession, Ct));

        // The messages of the day (contracts e-mailed, notifications, channel side) are all delivered: every contract
        // reaches its main driver, but the Booking.com customers, whom the channel gave no address.
        await DeliverTheOutboxAsync();
        var emails = TestEmails.ReadAll(host.Factory.EmailDirectory);
        _w.Contracts.Count.ShouldBe(7, "seven departures on the tablet");
        foreach (var (bookingId, contract) in _w.Contracts)
        {
            if (bookingId == _lateReturn || bookingId == _bookingComArrival)
            {
                contract.EmailedTo.ShouldBeNull(contract.Number);
                continue;
            }

            var address = contract.EmailedTo.ShouldNotBeNull(contract.Number);
            emails.ShouldContain(e => e.To.Contains(address, StringComparison.Ordinal) && e.Subject.Contains(contract.Number, StringComparison.Ordinal), contract.Number);
        }

        _w.JumpTo(0, 19, 15);
        await TheFilesEndAsExpectedAsync(rush);
        await NoVehicleAndNoCategoryIsEverOverbookedAsync();
        await ThePlanningOfTheDayListsEveryDepartureAndReturnAsync();
        await TheDashboardOfTheDayMatchesAsync(rush);
        await TheReadableNumbersStayGaplessAsync();
    }

    private async Task TheFilesEndAsExpectedAsync(IReadOnlyList<SaleOutcome> rush)
    {
        var expected = new Dictionary<Guid, BookingStatusInfo>
        {
            [_onTimeReturn] = BookingStatusInfo.Closed,
            [_lateReturn] = BookingStatusInfo.Closed,
            [_extension] = BookingStatusInfo.InProgress,
            [_nineOClockDeparture] = BookingStatusInfo.InProgress,
            [_elevenOClockDeparture] = BookingStatusInfo.InProgress,
            [_walkIn] = BookingStatusInfo.InProgress,
            [_bookingComArrival] = BookingStatusInfo.InProgress,
            [_noShow] = BookingStatusInfo.NoShow,
            [_upgrade] = BookingStatusInfo.Confirmed,
            [_cancelledWithFee] = BookingStatusInfo.Cancelled,
            [_cancelledOption] = BookingStatusInfo.Cancelled,
            [_expiringRequest] = BookingStatusInfo.Expired,
            [_validatedRequest] = BookingStatusInfo.Confirmed,
            [_refusedRequest] = BookingStatusInfo.Refused,
            [_incompleteRequest] = BookingStatusInfo.AwaitingPayment,
            [_option] = BookingStatusInfo.Confirmed,
            [_whatsAppRequest] = BookingStatusInfo.AwaitingPayment,
            [_weekendBookingCom] = BookingStatusInfo.Confirmed,
            [_weekendPhone] = BookingStatusInfo.Confirmed,
        };
        foreach (var outcome in rush.Where(o => o.IsSold))
        {
            expected[outcome.Result!.Value] = outcome.Attempt.Source switch
            {
                SaleSource.Web or SaleSource.Funnel => BookingStatusInfo.PendingValidation,
                SaleSource.Phone => BookingStatusInfo.OptionHeld,
                _ => BookingStatusInfo.Confirmed,
            };
        }

        var files = await _w.RunAsync<CarRentalDbContext, List<Booking>>(System, db => db.Set<Booking>().AsNoTracking().Where(b => b.AgencyId == _w.AgencyId).ToListAsync(Ct));
        files.Select(f => f.Id).ShouldBe(expected.Keys, ignoreOrder: true, "no file appeared beside the day's (the losers of the rush wrote nothing)");
        foreach (var file in files)
        {
            ((BookingStatusInfo)file.Status).ShouldBe(expected[file.Id], file.Number);
        }
    }

    /// <summary>Invariants 1 and 2 on the stored rows: no vehicle carries two files at once, no category more files than vehicles.</summary>
    private async Task NoVehicleAndNoCategoryIsEverOverbookedAsync()
    {
        var occupied = await OccupancyAudit.LoadAsync(host, _w.AgencyId, Ct);
        OccupancyAudit.NoVehicleCarriesTwoFiles(occupied);
        var now = _w.Clock.GetUtcNow();
        foreach (var category in _w.Categories)
        {
            var periods = occupied.Where(f => f.Booking.CategoryId == category.Id).Select(f => f.Period).ToList();
            OccupancyAudit.MaxConcurrency(periods).ShouldBeLessThanOrEqualTo(category.Vehicles.Count, category.Code);
            occupied.Where(f => f.Booking.CategoryId == category.Id && f.Period.End > now)
                .ShouldAllBe(f => f.Booking.AssignedVehicleId != null, $"every {category.Code} file of the coming days has its vehicle");

            var profile = await _w.RunAsync<IAvailabilityQueries, IReadOnlyList<CapacitySegmentInfo>?>(Manager, q => q.GetCapacityProfileAsync(category.Id, Period.Create(now, _w.At(60, 0)).Value, cancellationToken: Ct));
            profile.ShouldNotBeNull().ShouldAllBe(segment => segment.Remaining >= 0, $"{category.Code}: no shortfall from now on");
        }

        (await _w.RunAsync<IBookingsQueries, IReadOnlyList<CapacityShortfallInfo>>(Manager, q => q.GetCapacityShortfallsAsync(_w.AgencyId, Period.Create(now, _w.At(60, 0)).Value, Ct)))
            .ShouldBeEmpty();
    }

    /// <summary>The planning of the day: every departure and return on its vehicle row, no row with two files at once.</summary>
    private async Task ThePlanningOfTheDayListsEveryDepartureAndReturnAsync()
    {
        var day = Period.Create(_w.At(0, 0), _w.At(1, 0)).Value;
        var planning = await _w.RunAsync<IBookingsQueries, PlanningInfo>(Manager, q => q.GetPlanningAsync(_w.AgencyId, day, Ct));
        var rows = planning.Categories.SelectMany(c => c.Vehicles).ToList();
        planning.Categories.ShouldAllBe(c => c.Unassigned.Count == 0, "every file of the day has its vehicle");
        planning.Categories.ShouldAllBe(c => c.Shortfalls.Count == 0);
        foreach (var row in rows)
        {
            row.Bookings.ShouldAllBe(b => b.AssignedVehicleId == row.VehicleId);
            var bars = row.Bookings.OrderBy(b => b.Occupancy.Start).ToList();
            for (var i = 1; i < bars.Count; i++)
            {
                bars[i].Occupancy.Overlaps(bars[i - 1].Occupancy).ShouldBeFalse($"{row.PlateNumber}: {bars[i - 1].Number} and {bars[i].Number}");
            }
        }

        var onTheGrid = rows.SelectMany(r => r.Bookings.Select(b => (b.BookingId, r.VehicleId))).ToList();
        var agenda = await _w.RunAsync<IBookingsQueries, DayAgendaInfo>(Manager, q => q.GetDayAgendaAsync(_w.AgencyId, _w.Day, Ct));
        foreach (var item in agenda.Departures.Concat(agenda.Returns))
        {
            onTheGrid.ShouldContain((item.BookingId, item.VehicleId.ShouldNotBeNull()), item.Number);
        }

        onTheGrid.Select(b => b.BookingId).ShouldBe(
            [_onTimeReturn, _bookingComArrival, _nineOClockDeparture, _lateReturn, _walkIn, _elevenOClockDeparture, _extension],
            ignoreOrder: true,
            "the files that occupy a vehicle during the day, the no-show freed its own");
        onTheGrid.Single(b => b.BookingId == _onTimeReturn).VehicleId.ShouldBe(onTheGrid.Single(b => b.BookingId == _bookingComArrival).VehicleId, "the economy car back at 09:40 left again at 14:05");
    }

    /// <summary>The agenda, the dashboard and the hand-overs of the day tell the same day.</summary>
    private async Task TheDashboardOfTheDayMatchesAsync(IReadOnlyList<SaleOutcome> rush)
    {
        var departures = new[] { _nineOClockDeparture, _walkIn, _elevenOClockDeparture, _bookingComArrival };
        var returns = new[] { _lateReturn, _onTimeReturn };

        var dashboard = await DashboardAsync();
        dashboard.Today.Departures.Select(d => d.BookingId).ShouldBe(departures, "by planned pick-up; the no-show is no departure");
        dashboard.Today.Departures.ShouldAllBe(d => d.IsDone && !d.IsLate);
        dashboard.Today.Returns.Select(r => r.BookingId).ShouldBe(returns, "the extended SUV comes back tomorrow");
        dashboard.Today.Returns.ShouldAllBe(r => r.IsDone);
        dashboard.LateReturns.ShouldBeEmpty();
        dashboard.UnassignedSoon.ShouldBeEmpty();
        dashboard.Shortfalls.ShouldBeEmpty();
        var pending = rush.Where(o => o.IsSold && o.Attempt.Source is SaleSource.Web or SaleSource.Funnel).Select(o => o.Result!.Value).ToList();
        dashboard.Requests.ShouldNotBeNull().Select(r => r.BookingId).ShouldBe(pending, ignoreOrder: true, "the requests of the rush wait for tomorrow's queue");

        var handovers = await _w.OkAsync<HandoverQueries, HandoverDay>(Agent, q => q.GetDayAsync(_w.AgencyId, _w.Day, Ct));
        handovers.Departures.Select(d => d.BookingId).ShouldBe(departures);
        handovers.Departures.ShouldAllBe(d => d.IsDone);
        handovers.Returns.Select(r => r.BookingId).ShouldBe(returns);
        handovers.Returns.ShouldAllBe(r => r.IsDone);
        handovers.LateReturns.ShouldBeEmpty();
    }

    /// <summary>The LOC numbers of the year form 1…n although 145 attempts of the rush were rolled back (ADR-0016).</summary>
    private async Task TheReadableNumbersStayGaplessAsync()
    {
        var prefix = string.Create(CultureInfo.InvariantCulture, $"LOC-{_w.Day.Year}-");
        var numbers = await _w.RunAsync<CarRentalDbContext, List<string>>(System, db => db.Set<Booking>().AsNoTracking().Where(b => b.Number.StartsWith(prefix)).Select(b => b.Number).ToListAsync(Ct));
        numbers.Select(n => long.Parse(n[prefix.Length..], CultureInfo.InvariantCulture)).Order().ShouldBe(Enumerable.Range(1, numbers.Count).Select(i => (long)i));
    }

    /// <summary>Runs the outbox job until nothing is left (no job server on this host), then checks that nothing failed.</summary>
    private async Task DeliverTheOutboxAsync()
    {
        for (var run = 0; run < 20; run++)
        {
            await host.RunJobAsync("outbox.process", Ct);
            if (await _w.RunAsync<CarRentalDbContext, int>(System, db => db.Set<OutboxMessage>().CountAsync(m => m.ProcessedAtUtc == null, Ct)) == 0)
            {
                break;
            }
        }

        var failures = await _w.RunAsync<CarRentalDbContext, List<string>>(System, db => db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAtUtc == null || m.AttemptCount > 0)
            .Select(m => m.Type + ": " + (m.LastError ?? "pending"))
            .ToListAsync(Ct));
        failures.ShouldBeEmpty("every message of the day is delivered at its first attempt");
        (await _w.RunAsync<CarRentalDbContext, int>(System, db => db.Set<OutboxMessage>().CountAsync(Ct))).ShouldBeGreaterThan(100);
    }

    // ---------------------------------------------------------------------------------------------------------------

    private static UserContextSnapshot System { get; } = UserContextSnapshot.System("system:tests");

    private Task<ValidationOutcome> ValidateAsync(Guid bookingId) =>
        _w.OkAsync<BookingWorkflowService, ValidationOutcome>(Agent, s => s.ValidateAsync(bookingId, Ct));

    private Task<BookingsDashboard> DashboardAsync() =>
        _w.OkAsync<BookingAdministrationQueries, BookingsDashboard>(Agent, q => q.GetDashboardAsync(_w.AgencyId, Ct));

    private Task<Quote> QuoteOf(Guid bookingId) =>
        _w.RunAsync<CarRentalDbContext, Quote>(System, async db =>
        {
            var current = await db.Set<Booking>().AsNoTracking().Where(b => b.Id == bookingId).Select(b => b.CurrentQuoteId).SingleAsync(Ct);
            return await db.Set<Quote>().AsNoTracking().SingleAsync(q => q.Id == current, Ct);
        });

    /// <summary>An amendment from the current terms of the file, as the dialog of the file page sends it.</summary>
    private async Task<AmendmentResult> AmendAsync(Guid bookingId, Func<AmendBookingRequest, AmendBookingRequest> change)
    {
        var file = await _w.FileAsync(bookingId);
        var current = new AmendBookingRequest(
            file.Id,
            file.CategoryId,
            file.PickupLocationId,
            file.ReturnLocationId,
            file.PickupAt,
            file.ReturnAt,
            [],
            null,
            file.GuaranteedVehicleId,
            file.MainDriverId!.Value,
            file.SecondaryDriverId,
            file.PayerCustomerId,
            null);
        return await _w.OkAsync<BookingAmendmentService, AmendmentResult>(Agent, s => s.AmendAsync(change(current), Ct));
    }

    /// <summary>What the engine gives for the late day: the rental to the actual return minus the rental as planned, at the plan in force.</summary>
    private async Task<Money> LateDayByTheEngineAsync(Guid bookingId, DateTimeOffset returnedAt)
    {
        var file = await _w.FileAsync(bookingId);
        var request = new PriceRequest(file.AgencyId, file.CategoryId, file.PickupLocationId, file.ReturnLocationId, file.PickupAt, file.ReturnAt, file.Channel);
        var planned = await _w.OkAsync<IPricingService, PriceQuote>(Manager, p => p.QuoteAsync(request, Ct));
        var actual = await _w.OkAsync<IPricingService, PriceQuote>(Manager, p => p.QuoteAsync(request with { ReturnAt = returnedAt }, Ct));
        actual.Days.ShouldBe(planned.Days + 1);
        return actual.TotalInclVat - planned.TotalInclVat;
    }

    /// <summary>The attempts of the rush on <paramref name="category"/>, prepared before it: drivers registered, funnel sessions opened.</summary>
    private async Task<List<SaleAttempt>> AttemptsAsync(SeasonCategory category, params (SaleSource Source, int Count)[] mix)
    {
        var attempts = new List<SaleAttempt>();
        var index = 0;
        foreach (var (source, count) in mix)
        {
            for (var i = 0; i < count; i++, index++)
            {
                var (pickupAt, returnAt) = RushPeriod(category, index);
                attempts.Add(await AttemptAsync(source, category, pickupAt, returnAt));
            }
        }

        return attempts;
    }

    /// <summary>
    /// The period of the n-th attempt: every period of a category contains the same night, so that at most the vehicles
    /// left can be sold whatever the order of the winners.
    /// </summary>
    private (DateTimeOffset PickupAt, DateTimeOffset ReturnAt) RushPeriod(SeasonCategory category, int index) =>
        category == _w.Ecmr ? (_w.At(8, 10).AddHours(index % 11), _w.At(11, 10).AddHours(index % 7))
        : category == _w.Cdmr ? (_w.At(8, 12).AddHours(index % 9), _w.At(10, 12).AddHours(index % 8))
        : (_w.At(1, 10).AddHours(index % 3), _w.At(2, 10).AddHours(index % 3));

    private async Task<SaleAttempt> AttemptAsync(SaleSource source, SeasonCategory category, DateTimeOffset pickupAt, DateTimeOffset returnAt)
    {
        switch (source)
        {
            case SaleSource.Web:
                {
                    var driver = await _w.NewDriverAsync("Amel");
                    var draft = new BookingRequestDraft(SalesChannel.Web, Price: new PriceRequest(_w.AgencyId, category.Id, _w.OfficeId, _w.OfficeId, pickupAt, returnAt, SalesChannel.Web));
                    return new SaleAttempt(source, category, async () =>
                        (await _w.RunAsync<IBookingRequests, Result<BookingRequestResult>>(Funnel, r => r.SubmitRequestAsync(draft, new BookingRequestParties(driver.PersonId), Ct))).Map(r => r.BookingId));
                }

            case SaleSource.Funnel:
                {
                    var token = await _w.StartFunnelAsync(category, pickupAt, returnAt);
                    return new SaleAttempt(source, category, async () => (await _w.SubmitFunnelAsync(token)).Map(s => s.BookingId));
                }

            case SaleSource.Phone or SaleSource.Counter:
                {
                    var driver = await _w.NewDriverAsync("Mehdi");
                    var request = source == SaleSource.Phone
                        ? _w.Quick(category, driver.PersonId, pickupAt, returnAt, QuickBookingOutcome.Option)
                        : _w.Quick(category, driver.PersonId, pickupAt, returnAt, channel: SalesChannel.Counter);
                    return new SaleAttempt(source, category, async () =>
                        (await _w.RunAsync<QuickBookingService, Result<QuickBookingResult>>(Agent, s => s.CreateAsync(request, Ct))).Map(r => r.BookingId));
                }

            case SaleSource.BookingComExtranet:
                {
                    var entry = _w.Extranet(category, pickupAt, returnAt, gross: 450m);
                    return new SaleAttempt(source, category, async () =>
                        (await _w.RunAsync<ChannelManualEntryService, Result<ManualEntryResult>>(Manager, s => s.RecordAsync(entry, Ct))).Map(r => r.BookingId));
                }

            case SaleSource.BookingComConnected:
                {
                    var reservation = _w.FromBookingCom(category, pickupAt, returnAt);
                    return new SaleAttempt(source, category, async () =>
                        (await _w.RunAsync<IChannelBookings, Result<ChannelBookingResult>>(ChannelInbound, b => b.CreateConfirmedAsync(reservation, cancellationToken: Ct))).Map(r => r.BookingId));
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(source), source, null);
        }
    }
}
