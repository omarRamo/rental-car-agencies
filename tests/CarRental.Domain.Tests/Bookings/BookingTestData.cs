using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Bookings;

/// <summary>
/// Rental files in any status, built only through the aggregate's own commands, with a fixed calendar: created at
/// <see cref="Now"/>, picked up ten days later at 10:00 (Tunis), returned three days after.
/// </summary>
internal static class BookingTestData
{
    public static readonly DateTimeOffset Now = TunisTime.FromLocal(new DateTime(2027, 6, 1, 9, 0, 0, DateTimeKind.Unspecified));

    public static readonly DateTimeOffset PickupAt = TunisTime.FromLocal(new DateTime(2027, 6, 11, 10, 0, 0, DateTimeKind.Unspecified));

    public static readonly DateTimeOffset ReturnAt = PickupAt.AddDays(3);

    public static readonly TimeSpan Hold = TimeSpan.FromHours(24);

    public static readonly TimeSpan NoShowDelay = TimeSpan.FromHours(2);

    public static readonly Guid AgencyId = Guid.CreateVersion7();

    public static readonly Guid CategoryId = Guid.CreateVersion7();

    public static readonly Guid OtherCategoryId = Guid.CreateVersion7();

    public static readonly Guid OfficeId = Guid.CreateVersion7();

    public static readonly Guid AirportId = Guid.CreateVersion7();

    public static readonly Guid MainDriverId = Guid.CreateVersion7();

    public static readonly Guid SecondDriverId = Guid.CreateVersion7();

    public static readonly Guid PayerId = Guid.CreateVersion7();

    public static readonly Guid VehicleA = Guid.CreateVersion7();

    public static readonly Guid VehicleB = Guid.CreateVersion7();

    public static BookingActor Agent { get; } = new(Guid.CreateVersion7(), "agent@demo.local");

    public static BookingActor Manager { get; } = new(Guid.CreateVersion7(), "gerant@demo.local");

    public static BookingActor Customer { get; } = new(null, "customer", SalesChannel.Web);

    public static BookingActor Platform { get; } = BookingActor.System("system:tests");

    public static BookingParties Parties { get; } = new(MainDriverId, null, PayerId);

    private static int _sequence;

    public static DocumentNumber NextNumber() => DocumentNumber.Create(DocumentType.Booking, 2027, Interlocked.Increment(ref _sequence)).Value;

    public static BookingQuoteReference QuoteRef(int version = 1, decimal total = 450m) =>
        new(Guid.CreateVersion7(), version, Money.Tnd(total), Money.Tnd(600m));

    public static BookingDetails Details(
        SalesChannel channel = SalesChannel.Phone,
        BookingParties? parties = null,
        Guid? guaranteedVehicleId = null,
        DateTimeOffset? pickupAt = null,
        DateTimeOffset? returnAt = null,
        string? channelReference = null) => new(
        AgencyId,
        channel,
        CategoryId,
        OfficeId,
        OfficeId,
        pickupAt ?? PickupAt,
        returnAt ?? ReturnAt,
        parties ?? Parties,
        [ExtraChoice.Create("CHILD_SEAT", 1).Value],
        PromoCode: null,
        GuaranteedVehicleId: guaranteedVehicleId,
        ChannelReference: channelReference);

    public static Booking Draft(BookingParties? parties = null) =>
        Booking.CreateDraft(Guid.CreateVersion7(), Details(SalesChannel.Web, parties ?? BookingParties.None), NextNumber(), QuoteRef(), Customer, Now).Value;

    public static Booking Pending()
    {
        var booking = Draft();
        Expect(booking.Submit(Parties, Hold, Customer, Now));
        return booking;
    }

    public static Booking Option(TimeSpan? duration = null) =>
        Booking.CreateOption(Guid.CreateVersion7(), Details(), NextNumber(), QuoteRef(), Now + (duration ?? TimeSpan.FromHours(48)), Agent, Now).Value;

    public static Booking Confirmed(Guid? guaranteedVehicleId = null) =>
        Booking.CreateConfirmed(Guid.CreateVersion7(), Details(guaranteedVehicleId: guaranteedVehicleId), NextNumber(), QuoteRef(), Agent, Now).Value;

    /// <summary>A file in <paramref name="status"/>, reached through legal commands only.</summary>
    public static Booking InStatus(BookingStatus status)
    {
        Booking booking;
        switch (status)
        {
            case BookingStatus.Draft:
                return Draft();
            case BookingStatus.PendingValidation:
                return Pending();
            case BookingStatus.InformationRequested:
                booking = Pending();
                Expect(booking.RequestInformation(InformationRequestReason.MissingDocuments, "Merci de déposer le verso du permis.", Hold, Agent, Now.AddHours(1)));
                return booking;
            case BookingStatus.OptionHeld:
                return Option();
            case BookingStatus.AwaitingPayment:
                booking = Pending();
                Expect(booking.Validate(Now.AddHours(25), Agent, Now.AddHours(1)));
                return booking;
            case BookingStatus.Confirmed:
                return Confirmed();
            case BookingStatus.InProgress:
                booking = Confirmed();
                Expect(booking.AssignVehicle(VehicleA, null, null, Agent, Now.AddHours(1)));
                Expect(booking.StartRental(PickupAt, Agent, PickupAt));
                return booking;
            case BookingStatus.Returned:
                booking = InStatus(BookingStatus.InProgress);
                Expect(booking.CompleteReturn(ReturnAt, Agent, ReturnAt));
                return booking;
            case BookingStatus.Closed:
                booking = InStatus(BookingStatus.Returned);
                Expect(booking.Close(Agent, ReturnAt.AddHours(2)));
                return booking;
            case BookingStatus.Refused:
                booking = Pending();
                Expect(booking.Refuse("Permis trop récent", Agent, Now.AddHours(1)));
                return booking;
            case BookingStatus.Expired:
                booking = Pending();
                Expect(booking.Expire(Platform, Now + Hold));
                return booking;
            case BookingStatus.Cancelled:
                booking = Confirmed();
                Expect(booking.Cancel("Le client annule", Money.Zero, feeWaived: false, Agent, Now.AddHours(1)));
                return booking;
            case BookingStatus.NoShow:
                booking = Confirmed();
                Expect(booking.MarkNoShow("Client injoignable", NoShowDelay, Agent, PickupAt + NoShowDelay));
                return booking;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }
    }

    public static void Expect(Result result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
    }

    /// <summary>A consistent frozen content: <paramref name="days"/> days at <paramref name="dailyPrice"/> TTC and the daily contribution.</summary>
    public static QuoteContent Content(int days = 3, decimal dailyPrice = 100m, DateTimeOffset? returnAt = null, decimal contributionExclVat = 2m)
    {
        const decimal rate = 0.19m;
        var rental = Money.Tnd(dailyPrice * days);
        var rentalExclVat = Money.Tnd(rental.Amount / (1m + rate));
        var contributionHt = Money.Tnd(contributionExclVat * days);
        var contributionVat = contributionHt * rate;
        QuoteLineData[] lines =
        [
            new(QuoteLineKind.Rental, "RENTAL", "Quote.Line.Rental.MidSeason", [], days, Money.Tnd(dailyPrice), rentalExclVat, rental - rentalExclVat, rental, rate),
            new(
                QuoteLineKind.DailyContribution,
                "DAILY_CONTRIBUTION",
                "Quote.Line.DailyContribution",
                ["2.000"],
                days,
                Money.Tnd(contributionExclVat * (1m + rate)),
                contributionHt,
                contributionVat,
                contributionHt + contributionVat,
                rate),
        ];
        return new QuoteContent(
            AgencyId,
            CategoryId,
            SalesChannel.Phone,
            OfficeId,
            OfficeId,
            PickupAt,
            returnAt ?? PickupAt.AddDays(days),
            Guid.CreateVersion7(),
            1,
            days,
            lines,
            lines.Select(l => l.ExclVat).Sum(),
            lines.Select(l => l.Vat).Sum(),
            lines.Select(l => l.InclVat).Sum(),
            rental,
            Money.Tnd(600m),
            IncludesFranchiseWaiver: false,
            QuotePromoCodeOutcome.None,
            PromoCode: null,
            AppliedPromotionsJson: "[]",
            InputsJson: "{\"days\":" + days + "}",
            DailyRatesJson: "[]");
    }
}
