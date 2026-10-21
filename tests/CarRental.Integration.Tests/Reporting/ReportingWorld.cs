using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using CarRental.Integration.Tests.Fleet;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Reporting;

/// <summary>A test agency of its own (tests run in parallel on one database) with one ECMR category and its vehicles.</summary>
internal sealed record ReportingAgency(Guid AgencyId, Guid CategoryId, IReadOnlyList<Guid> Vehicles);

/// <summary>What became of a past rental file written by <see cref="ReportingWorld.PastFileAsync"/>.</summary>
internal enum PastOutcome
{
    Confirmed,
    InProgress,
    Returned,
    Closed,
    Cancelled,
    NoShow,
}

/// <summary>
/// Data of the reporting tests: agencies, vehicles and rental files in the past. The services refuse a pick-up in the
/// past, so past files are written straight on the aggregates with their own instants, like the demo history: a frozen
/// quote of one rental line (VAT 19 %) and the daily contribution (2.000 HT per day), then the steps of the file.
/// </summary>
internal static class ReportingWorld
{
    public static UserContextSnapshot Manager { get; } = FleetData.Actor(Roles.Manager, "gerant.rapports@demo.local");

    public static UserContextSnapshot Accountant { get; } = FleetData.Actor(Roles.Accountant, "comptable.rapports@demo.local");

    public static UserContextSnapshot Agent { get; } = FleetData.Actor(Roles.Agent, "agent.rapports@demo.local");

    /// <summary>Today in Tunis, plus <paramref name="days"/>.</summary>
    public static DateOnly Day(int days) => TunisTime.Today(TimeProvider.System).AddDays(days);

    /// <summary>A Tunis instant <paramref name="days"/> days from today at <paramref name="hour"/>:00.</summary>
    public static DateTimeOffset At(int days, int hour = 10) => TunisTime.FromLocal(Day(days), new TimeOnly(hour, 0));

    public static async Task<ReportingAgency> CreateAsync(IntegrationHost host, int vehicles, CancellationToken cancellationToken)
    {
        var agencyId = await FleetData.NewAgencyAsync(host, cancellationToken);
        var category = await FleetData.NewCategoryAsync(host, agencyId, "ECMR", cancellationToken);
        var ids = new List<Guid>();
        for (var i = 0; i < vehicles; i++)
        {
            ids.Add(await FleetData.NewVehicleAsync(host, category, cancellationToken));
        }

        return new ReportingAgency(agencyId, category, ids);
    }

    /// <summary>
    /// A rental file of <paramref name="days"/> days picked up at <paramref name="pickupAt"/> (in the past), carried by
    /// <paramref name="vehicle"/>, priced <paramref name="rentalInclVat"/> TTC for the rental (HT = TTC ÷ 1.19) plus the
    /// daily contribution, and taken to <paramref name="outcome"/>. Returns the file.
    /// </summary>
    public static async Task<Booking> PastFileAsync(
        IntegrationHost host,
        ReportingAgency agency,
        Guid? vehicle,
        DateTimeOffset pickupAt,
        int days,
        decimal rentalInclVat,
        PastOutcome outcome,
        CancellationToken cancellationToken,
        SalesChannel channel = SalesChannel.Phone,
        Guid? swapTo = null,
        Money? cancellationFee = null)
    {
        var id = Entity.NewId();
        var createdAt = pickupAt.AddDays(-5);
        var content = Content(agency, channel, pickupAt, days, rentalInclVat);
        var quote = Quote.CreateVersion(id, 1, QuoteOrigin.Initial, content, QuoteCode.Generate(), "tests", createdAt).Value;
        // A synthetic year far from today: the gapless numbering test reads every booking number of the current year.
        var number = DocumentNumber.Create(DocumentType.Booking, Random.Shared.Next(5000, 9000), Random.Shared.NextInt64(1, 999_999)).Value;
        var actor = new BookingActor(Manager.UserId, Manager.UserName, channel);
        var location = Guid.CreateVersion7();
        var details = new BookingDetails(
            agency.AgencyId,
            channel,
            agency.CategoryId,
            location,
            location,
            pickupAt,
            pickupAt.AddDays(days),
            new BookingParties(Guid.CreateVersion7(), null, Guid.CreateVersion7()));
        var booking = Booking.CreateConfirmed(id, details, number, quote.ToReference(), actor, createdAt).Value;
        if (vehicle is { } assigned)
        {
            Ok(booking.AssignVehicle(assigned, null, null, actor, createdAt.AddMinutes(5)));
        }

        var returnAt = pickupAt.AddDays(days).AddMinutes(-20);
        switch (outcome)
        {
            case PastOutcome.InProgress or PastOutcome.Returned or PastOutcome.Closed:
                Ok(booking.StartRental(pickupAt, actor, pickupAt));
                if (swapTo is { } replacement)
                {
                    Ok(booking.AssignVehicle(replacement, null, "Panne de climatisation", actor, pickupAt.AddDays(1).AddHours(4)));
                }

                if (outcome != PastOutcome.InProgress)
                {
                    Ok(booking.CompleteReturn(returnAt, actor, returnAt));
                }

                if (outcome == PastOutcome.Closed)
                {
                    Ok(booking.Close(actor, returnAt.AddMinutes(30)));
                }

                break;
            case PastOutcome.Cancelled:
                Ok(booking.Cancel("Voyage annulé", cancellationFee ?? Money.Zero, false, actor, pickupAt.AddDays(-1)));
                break;
            case PastOutcome.NoShow:
                Ok(booking.MarkNoShow("Client absent", TimeSpan.FromHours(2), actor, pickupAt.AddHours(3)));
                break;
        }

        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        db.Set<Booking>().Add(booking);
        db.Set<Quote>().Add(quote);
        await db.SaveChangesAsync(cancellationToken);
        return booking;
    }

    /// <summary>Rental revenue HT of a quote built by <see cref="PastFileAsync"/>: the rental line excluding VAT.</summary>
    public static Money RentalExclVat(decimal rentalInclVat) => Money.Tnd(rentalInclVat / 1.19m);

    private static QuoteContent Content(ReportingAgency agency, SalesChannel channel, DateTimeOffset pickupAt, int days, decimal rentalInclVat)
    {
        var rentalTtc = Money.Tnd(rentalInclVat);
        var rentalHt = RentalExclVat(rentalInclVat);
        var contributionHt = Money.Tnd(2m * days);
        var contributionVat = Money.Tnd(2m * days * 0.19m);
        QuoteLineData[] lines =
        [
            new(QuoteLineKind.Rental, "RENTAL", "Pricing.Line.Rental", [], days, Money.Tnd(rentalInclVat / days), rentalHt, rentalTtc - rentalHt, rentalTtc, 0.19m),
            new(QuoteLineKind.DailyContribution, "DAILY_CONTRIBUTION", "Pricing.Line.DailyContribution", [], days, Money.Tnd(2.38m), contributionHt, contributionVat, contributionHt + contributionVat, 0.19m),
        ];
        return new QuoteContent(
            agency.AgencyId,
            agency.CategoryId,
            channel,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            pickupAt,
            pickupAt.AddDays(days),
            Guid.CreateVersion7(),
            1,
            days,
            lines,
            lines.Select(l => l.ExclVat).Sum(),
            lines.Select(l => l.Vat).Sum(),
            lines.Select(l => l.InclVat).Sum(),
            rentalTtc,
            Money.Tnd(600m),
            false,
            QuotePromoCodeOutcome.None,
            null,
            "[]",
            "{}",
            "[]");
    }

    private static void Ok(Result result) => result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
}
