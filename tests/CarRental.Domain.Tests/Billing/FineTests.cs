using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Billing.BillingTestData;

namespace CarRental.Domain.Tests.Billing;

/// <summary>
/// Traffic fines: the rental that had the vehicle at the instant of the offence (actual pick-up to actual return, open
/// while in progress) gives the file and its main driver; none or several are left to an agent. Then designation,
/// re-invoicing, payment and closing.
/// </summary>
public sealed class FineTests
{
    private static readonly DateTimeOffset Offence = new(2026, 10, 10, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid VehicleId = Guid.CreateVersion7();
    private static readonly Guid MainDriver = Guid.CreateVersion7();
    private static readonly Guid SecondDriver = Guid.CreateVersion7();

    private static RentalCandidate Rental(DateTimeOffset from, DateTimeOffset? until, string number = "LOC-2026-000010") =>
        new(Guid.CreateVersion7(), number, from, until, MainDriver, SecondDriver);

    private static FineDetails Details(decimal amount = 60m) => new("PV-2026-445566", "Garde nationale", Offence, "Route de Bizerte, km 12", "123 TU 4567", amount, "Excès de vitesse");

    private static Fine Recorded(FineMatchResult match) => Fine.Record(AgencyId, Details(), match, Accountant, Now).Value;

    [Fact]
    public void The_one_rental_covering_the_instant_gives_the_file_and_its_main_driver()
    {
        var before = Rental(Offence.AddDays(-9), Offence.AddDays(-5), "LOC-2026-000001");
        var during = Rental(Offence.AddDays(-1), Offence.AddDays(1), "LOC-2026-000002");

        var match = FineMatching.Match(VehicleId, Offence, [before, during]);

        (match.Match, match.Rental).ShouldBe((FineMatch.Matched, during));
        var fine = Recorded(match);
        (fine.Match, fine.BookingId, fine.BookingNumber, fine.DriverPersonId).ShouldBe((FineMatch.Matched, during.BookingId, "LOC-2026-000002", MainDriver));
        fine.HasRental.ShouldBeTrue();
        fine.DomainEvents.OfType<FineRecorded>().ShouldHaveSingleItem().BookingId.ShouldBe(during.BookingId);
    }

    [Fact]
    public void A_rental_covers_its_actual_pick_up_but_not_its_actual_return_and_stays_open_while_in_progress()
    {
        Rental(Offence, Offence.AddDays(1)).Covers(Offence).ShouldBeTrue();
        Rental(Offence.AddDays(-1), Offence).Covers(Offence).ShouldBeFalse("the vehicle was back at that instant");
        Rental(Offence.AddDays(-1), null).Covers(Offence).ShouldBeTrue("a rental in progress has the vehicle");
        Rental(Offence.AddMinutes(1), null).Covers(Offence).ShouldBeFalse();
    }

    [Fact]
    public void None_or_several_covering_rentals_and_an_unknown_plate_are_left_to_an_agent()
    {
        FineMatching.Match(VehicleId, Offence, [Rental(Offence.AddDays(1), null)]).Match.ShouldBe(FineMatch.NoRental);
        FineMatching.Match(null, Offence, []).Match.ShouldBe(FineMatch.UnknownVehicle);

        var first = Rental(Offence.AddDays(-2), Offence.AddHours(1));
        var second = Rental(Offence.AddHours(-1), Offence.AddDays(2));
        var ambiguous = FineMatching.Match(VehicleId, Offence, [first, second]);

        ambiguous.Match.ShouldBe(FineMatch.Ambiguous);
        ambiguous.Candidates.ShouldBe([first.BookingId, second.BookingId], ignoreOrder: true);
        var fine = Recorded(ambiguous);
        fine.BookingId.ShouldBeNull();
        fine.CandidateBookingIds.ShouldBe([first.BookingId, second.BookingId], ignoreOrder: true);
        fine.HasRental.ShouldBeFalse();
    }

    [Fact]
    public void An_agent_chooses_a_file_that_had_the_vehicle_and_one_of_its_drivers()
    {
        var fine = Recorded(FineMatching.Match(VehicleId, Offence, []));
        var covering = Rental(Offence.AddDays(-1), Offence.AddDays(1));

        fine.AssignRental(Rental(Offence.AddDays(1), null), MainDriver, Accountant, Now).Error.ShouldBe(BillingErrors.FineRentalNotCovering);
        fine.AssignRental(covering, Guid.CreateVersion7(), Accountant, Now).Error.ShouldBe(BillingErrors.FineDriverInvalid);
        fine.AssignRental(covering, SecondDriver, Accountant, Now).IsSuccess.ShouldBeTrue();

        (fine.Match, fine.BookingId, fine.DriverPersonId).ShouldBe((FineMatch.Manual, covering.BookingId, SecondDriver));
        fine.CandidateBookingIds.ShouldBeEmpty();
    }

    [Fact]
    public void The_notice_is_checked()
    {
        Fine.Record(AgencyId, Details() with { TicketNumber = " " }, FineMatching.Match(VehicleId, Offence, []), Accountant, Now).Error.ShouldBe(BillingErrors.FineTicketRequired);
        Fine.Record(AgencyId, Details() with { Authority = null }, FineMatching.Match(VehicleId, Offence, []), Accountant, Now).Error.ShouldBe(BillingErrors.FineAuthorityRequired);
        Fine.Record(AgencyId, Details() with { Place = "" }, FineMatching.Match(VehicleId, Offence, []), Accountant, Now).Error.ShouldBe(BillingErrors.FinePlaceRequired);
        Fine.Record(AgencyId, Details() with { PlateNumber = " - " }, FineMatching.Match(VehicleId, Offence, []), Accountant, Now).Error.ShouldBe(BillingErrors.FinePlateRequired);
        Fine.Record(AgencyId, Details(0m), FineMatching.Match(VehicleId, Offence, []), Accountant, Now).Error.ShouldBe(BillingErrors.FineAmountInvalid);
        Fine.Record(AgencyId, Details(10.0001m), FineMatching.Match(VehicleId, Offence, []), Accountant, Now).Error.ShouldBe(BillingErrors.FineAmountInvalid);
        Fine.Record(AgencyId, Details() with { OffenceAt = Now.AddMinutes(1) }, FineMatching.Match(VehicleId, Offence, []), Accountant, Now).Error.ShouldBe(BillingErrors.FineOffenceInFuture);

        var fine = Recorded(FineMatching.Match(VehicleId, Offence, []));
        (fine.PlateNumber, fine.PlateKey, fine.Amount).ShouldBe(("123 TU 4567", "123TU4567", Money.Tnd(60m)));
    }

    [Fact]
    public void A_fine_is_designated_re_invoiced_paid_then_closed()
    {
        var fine = Recorded(FineMatching.Match(VehicleId, Offence, [Rental(Offence.AddDays(-1), null)]));

        fine.Designate(Accountant, Now).IsSuccess.ShouldBeTrue();
        fine.Designate(Accountant, Now.AddHours(1)).IsSuccess.ShouldBeTrue("the form can be produced again");
        fine.DesignatedAtUtc.ShouldBe(Now);
        fine.AssignRental(Rental(Offence.AddDays(-1), null), MainDriver, Accountant, Now).Error.ShouldBe(BillingErrors.FineInvalidTransition);

        var invoiceId = Guid.CreateVersion7();
        fine.MarkReinvoiced(invoiceId, "FAC-2026-000050", Accountant, Now).IsSuccess.ShouldBeTrue();
        fine.MarkReinvoiced(Guid.CreateVersion7(), "FAC-2026-000051", Accountant, Now).Error.ShouldBe(BillingErrors.FineAlreadyReinvoiced);
        (fine.Status, fine.InvoiceId, fine.InvoiceNumber).ShouldBe((FineStatus.Reinvoiced, invoiceId, "FAC-2026-000050"));

        fine.MarkPaid(Accountant, Now).IsSuccess.ShouldBeTrue();
        fine.Close("Payée par l'agence, refacturée au client", Accountant, Now).IsSuccess.ShouldBeTrue();
        fine.Close(null, Accountant, Now).Error.ShouldBe(BillingErrors.FineInvalidTransition);
        fine.MarkPaid(Accountant, Now).Error.ShouldBe(BillingErrors.FineInvalidTransition);
        fine.DomainEvents.OfType<FineStatusChanged>().Select(e => e.Status).ShouldBe([FineStatus.Designated, FineStatus.Reinvoiced, FineStatus.Paid, FineStatus.Closed]);
    }

    [Fact]
    public void Without_a_file_and_a_driver_a_fine_is_neither_designated_nor_re_invoiced()
    {
        var fine = Recorded(FineMatching.Match(VehicleId, Offence, []));

        fine.Designate(Accountant, Now).Error.ShouldBe(BillingErrors.FineNoRental);
        fine.MarkReinvoiced(Guid.CreateVersion7(), "FAC-2026-000050", Accountant, Now).Error.ShouldBe(BillingErrors.FineNoRental);
        fine.Close("Véhicule de l'agence", Accountant, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Plate_keys_ignore_spaces_dashes_and_case()
    {
        PlateKeys.Of("123 tu 4567").ShouldBe("123TU4567");
        PlateKeys.Of("123-TU-4567").ShouldBe(PlateKeys.Of("123TU4567"));
        PlateKeys.Of(null).ShouldBeEmpty();
    }
}
