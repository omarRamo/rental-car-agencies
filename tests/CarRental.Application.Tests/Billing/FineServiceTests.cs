using CarRental.Application.Billing.Contracts;
using CarRental.Application.Billing.Fines;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Paging;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Billing.Fakes;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Billing;

/// <summary>
/// Fines through the service (ADR-0059): the plate gives the vehicle, the instant the rental and its driver; the
/// designation form is personal data (documents read, every production logged); re-invoicing issues an invoice once.
/// </summary>
public sealed class FineServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Offence = BillingHarness.Now.AddDays(-3);

    private static FineForm Form(string plate = "123tu4567", string ticket = "PV-445566") =>
        new(BillingHarness.AgencyId, ticket, "Garde nationale", Offence, "Route de Bizerte", plate, 60m, null);

    private static VehicleRentalInfo Rental(BookingBillingInfo file, DateTimeOffset from, DateTimeOffset? until) =>
        new(file.BookingId, file.Number, file.AgencyId, file.Status, BillingHarness.VehicleId, from, until, file.MainDriverId, file.SecondaryDriverId, file.PayerCustomerId);

    [Fact]
    public async Task A_fine_finds_the_vehicle_by_its_plate_and_the_file_that_had_it_at_that_instant()
    {
        var harness = new BillingHarness().As(Roles.Agent);
        var file = harness.ClosedFile();
        harness.Bookings.Rentals.Add(Rental(file, Offence.AddDays(-2), Offence.AddDays(2)));

        var fineId = (await harness.Fines.RecordAsync(Form(), Ct)).Value;

        var detail = (await harness.Fines.GetAsync(fineId, Ct)).Value;
        (detail.Match, detail.VehicleId, detail.BookingId, detail.DriverPersonId, detail.DriverName)
            .ShouldBe((FineMatch.Matched, (Guid?)BillingHarness.VehicleId, (Guid?)file.BookingId, (Guid?)BillingHarness.DriverId, "Sami Ben Salah"));
        detail.Actions.CanProduceDesignation.ShouldBeTrue();
        (await harness.Fines.RecordAsync(Form(), Ct)).Error.ShouldBe(BillingErrors.FineDuplicate);

        var listed = (await harness.Fines.ListAsync(new FineListFilter(BillingHarness.AgencyId, Search: "123 TU"), new PageRequest(), Ct)).Value;
        listed.Items.ShouldHaveSingleItem().Id.ShouldBe(fineId);
    }

    [Fact]
    public async Task Without_one_covering_rental_the_fine_waits_for_an_agent_who_chooses_among_the_covering_ones()
    {
        var harness = new BillingHarness().As(Roles.Agent);
        var first = harness.ClosedFile();
        var second = harness.ClosedFile();
        harness.Bookings.Rentals.Add(Rental(first, Offence.AddDays(-2), Offence.AddHours(1)));
        harness.Bookings.Rentals.Add(Rental(second, Offence.AddHours(-1), null));

        var ambiguous = (await harness.Fines.RecordAsync(Form(), Ct)).Value;
        var unknown = (await harness.Fines.RecordAsync(Form("999 TU 9999", "PV-2"), Ct)).Value;

        var detail = (await harness.Fines.GetAsync(ambiguous, Ct)).Value;
        detail.Match.ShouldBe(FineMatch.Ambiguous);
        detail.Candidates.Select(c => c.BookingId).ShouldBe([first.BookingId, second.BookingId], ignoreOrder: true);
        detail.Candidates[0].Drivers.ShouldHaveSingleItem().IsMainDriver.ShouldBeTrue();
        detail.Actions.CanAssign.ShouldBeTrue();
        (await harness.Fines.GetAsync(unknown, Ct)).Value.Match.ShouldBe(FineMatch.UnknownVehicle);
        (await harness.Fines.ListAsync(new FineListFilter(OnlyToAssign: true), new PageRequest(), Ct)).Value.TotalCount.ShouldBe(2);

        (await harness.Fines.AssignAsync(ambiguous, Guid.CreateVersion7(), BillingHarness.DriverId, Ct)).Error.ShouldBe(BillingErrors.FineRentalNotCovering);
        (await harness.Fines.AssignAsync(ambiguous, second.BookingId, BillingHarness.DriverId, Ct)).IsSuccess.ShouldBeTrue();
        var assigned = (await harness.Fines.GetAsync(ambiguous, Ct)).Value;
        (assigned.Match, assigned.BookingId).ShouldBe((FineMatch.Manual, (Guid?)second.BookingId));
    }

    [Fact]
    public async Task The_designation_form_prints_the_documents_in_full_and_every_production_is_logged()
    {
        var harness = new BillingHarness().As(Roles.Agent);
        var file = harness.ClosedFile();
        harness.Bookings.Rentals.Add(Rental(file, Offence.AddDays(-2), Offence.AddDays(2)));
        var fineId = (await harness.Fines.RecordAsync(Form(), Ct)).Value;

        var form = (await harness.Fines.ProduceDesignationFormAsync(fineId, Ct)).Value;
        await harness.Fines.ProduceDesignationFormAsync(fineId, Ct);

        form.FileName.ShouldBe("designation-PV-445566.pdf");
        var text = System.Text.Encoding.UTF8.GetString(form.Content);
        text.ShouldContain("Sami Ben Salah");
        text.ShouldContain("12/345678", customMessage: "the licence number in full, for the authority");
        harness.AccessLog.Entries.Count.ShouldBe(4, "two documents, two productions");
        harness.AccessLog.Entries.ShouldAllBe(e => e.Access == FineService.DesignationAccess && e.Reason == "fine:PV-445566");
        harness.AccessLog.Entries.Select(e => e.Kind).Distinct().ShouldBe(["identity_card", "driving_licence"], ignoreOrder: true);
        harness.Audit.Events.Count(e => e.Action == FineService.DesignationAuditAction).ShouldBe(2);
        harness.Db.Rows<Fine>().Single().Status.ShouldBe(FineStatus.Recorded, "producing the form does not send it");

        (await harness.Fines.MarkDesignatedAsync(fineId, Ct)).IsSuccess.ShouldBeTrue();
        harness.Db.Rows<Fine>().Single().Status.ShouldBe(FineStatus.Designated);

        // The accountant has no access to the identity documents: no designation form.
        (await harness.As(Roles.Accountant).Fines.ProduceDesignationFormAsync(fineId, Ct)).Error.ShouldBe(BillingErrors.Forbidden);
    }

    [Fact]
    public async Task A_fine_is_re_invoiced_once_with_its_management_fee()
    {
        var harness = new BillingHarness().As(Roles.Agent);
        harness.Settings.Values[BillingSettings.FineManagementFee.Key] = Money.Tnd(23.8m);
        var file = harness.ClosedFile();
        harness.Bookings.Rentals.Add(Rental(file, Offence.AddDays(-2), Offence.AddDays(2)));
        var fineId = (await harness.Fines.RecordAsync(Form(), Ct)).Value;

        var issued = (await harness.Fines.ReinvoiceAsync(fineId, Ct)).Value;
        var again = (await harness.Fines.ReinvoiceAsync(fineId, Ct)).Value;

        again.ShouldBe(issued with { AlreadyIssued = true });
        var invoice = harness.Db.Rows<Invoice>().ShouldHaveSingleItem();
        (invoice.Kind, invoice.FineId, invoice.BookingId).ShouldBe((InvoiceKind.FineReinvoicing, (Guid?)fineId, (Guid?)file.BookingId));
        invoice.Lines.Select(l => (l.Nature, l.VatRate, l.InclVat)).ShouldBe([
            (InvoiceLineNature.FineReinvoicing, 0m, Money.Tnd(60m)),
            (InvoiceLineNature.ManagementFee, 0.19m, Money.Tnd(23.8m)),
        ]);
        invoice.VatBreakdown().Select(v => (v.Rate, v.Vat)).ShouldBe([(0m, Money.Zero), (0.19m, Money.Tnd(3.8m))]);
        var fine = harness.Db.Rows<Fine>().Single();
        (fine.Status, fine.InvoiceId, fine.InvoiceNumber).ShouldBe((FineStatus.Reinvoiced, (Guid?)issued.Id, issued.Number));

        (await harness.As(Roles.Preparer).Fines.ReinvoiceAsync(fineId, Ct)).Error.ShouldBe(BillingErrors.Forbidden);
    }

    [Fact]
    public async Task A_fine_without_its_file_is_neither_designated_nor_re_invoiced_and_can_be_closed()
    {
        var harness = new BillingHarness().As(Roles.Agent);
        var fineId = (await harness.Fines.RecordAsync(Form(), Ct)).Value;

        (await harness.Fines.ProduceDesignationFormAsync(fineId, Ct)).Error.ShouldBe(BillingErrors.FineNoRental);
        (await harness.Fines.ReinvoiceAsync(fineId, Ct)).Error.ShouldBe(BillingErrors.FineNoRental);
        (await harness.Fines.MarkPaidAsync(fineId, Ct)).IsSuccess.ShouldBeTrue();
        (await harness.Fines.CloseAsync(fineId, "Véhicule utilisé par l'agence", Ct)).IsSuccess.ShouldBeTrue();
        harness.Db.Rows<Fine>().Single().Status.ShouldBe(FineStatus.Closed);
    }
}
