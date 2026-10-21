using CarRental.Application.Billing.Claims;
using CarRental.Application.Common.Files;
using CarRental.Application.Common.Paging;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Billing.Fakes;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Billing;

/// <summary>Claims through the service: a vehicle of the agency, its file by number, costs and reimbursements, files checked by their type.</summary>
public sealed class ClaimServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 0x0A];

    private static ClaimForm Form(string? bookingNumber = null) =>
        new(BillingHarness.AgencyId, BillingHarness.VehicleId, bookingNumber, BillingHarness.Now.AddDays(-1), "Rond-point", "Accrochage arrière", "STAR", "SIN-1", 500m);

    [Theory]
    [InlineData(@"C:\scans\constat.pdf", "constat.pdf")]
    [InlineData("/home/agent/photos/arriere.jpg", "arriere.jpg")]
    [InlineData(" arriere.jpg ", "arriere.jpg")]
    [InlineData("dossier/", "")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void The_name_of_a_file_keeps_no_folder_whatever_the_system_of_the_device(string? sent, string? kept) =>
        ClaimFiles.NameOf(sent).ShouldBe(kept);

    [Fact]
    public async Task A_claim_is_opened_for_a_vehicle_of_the_agency_and_its_file()
    {
        var harness = new BillingHarness().As(Roles.Agent);
        var file = harness.ClosedFile();

        var claimId = (await harness.Claims.OpenAsync(Form(file.Number), Ct)).Value;

        var detail = (await harness.Claims.GetAsync(claimId, Ct)).Value;
        (detail.BookingId, detail.BookingNumber, detail.VehicleLabel).ShouldBe(((Guid?)file.BookingId, file.Number, "123 TU 4567 · Renault Clio"));
        (await harness.Claims.OpenAsync(Form() with { VehicleId = Guid.CreateVersion7() }, Ct)).Error.ShouldBe(BillingErrors.ClaimVehicleRequired);
        (await harness.Claims.OpenAsync(Form("LOC-2026-999999"), Ct)).Error.ShouldBe(BillingErrors.BookingNotFound);
        (await harness.As(Roles.Preparer).Claims.OpenAsync(Form(), Ct)).Error.ShouldBe(BillingErrors.Forbidden);
    }

    [Fact]
    public async Task Costs_and_reimbursements_make_the_net_cost_that_reaches_the_profitability_of_the_vehicle()
    {
        var harness = new BillingHarness().As(Roles.Accountant);
        var claimId = (await harness.Claims.OpenAsync(Form(), Ct)).Value;

        await harness.Claims.AddCostAsync(claimId, new ClaimEntryInput(new DateOnly(2026, 10, 20), "Carrosserie", 1_800m, "F-12"), Ct);
        await harness.Claims.AddReimbursementAsync(claimId, new ClaimEntryInput(new DateOnly(2026, 10, 28), "Assureur", 1_300m), Ct);

        var detail = (await harness.Claims.GetAsync(claimId, Ct)).Value;
        detail.NetCost.ShouldBe(Money.Tnd(500m));
        var listed = (await harness.Claims.ListAsync(new ClaimListFilter(BillingHarness.AgencyId), new PageRequest(), Ct)).Value.Items.ShouldHaveSingleItem();
        listed.NetCost.ShouldBe(Money.Tnd(500m));

        var costs = await harness.ClaimCosts.GetCostsAsync(BillingHarness.AgencyId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), Ct);
        costs.Select(c => (c.VehicleId, c.Amount, c.From)).ShouldBe(
            [(BillingHarness.VehicleId, Money.Tnd(1_800m), new DateOnly(2026, 10, 20)), (BillingHarness.VehicleId, Money.Tnd(-1_300m), new DateOnly(2026, 10, 28))],
            ignoreOrder: true);
        (await harness.ClaimCosts.GetCostsAsync(BillingHarness.AgencyId, new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 27), Ct)).ShouldBeEmpty();
        harness.ClaimCosts.Name.ShouldBe("claims");
    }

    [Fact]
    public async Task A_file_is_stored_by_its_real_type_and_removed_after_the_commit()
    {
        var harness = new BillingHarness().As(Roles.Agent);
        var claimId = (await harness.Claims.OpenAsync(Form(), Ct)).Value;

        (await harness.Claims.UploadAsync(claimId, ClaimDocumentKind.Photo, "photo.jpg", new MemoryStream([0x47, 0x49, 0x46, 0x38]), Ct)).Error.ShouldBe(BillingErrors.ClaimFileInvalid);
        harness.Storage.Files.ShouldBeEmpty();
        var documentId = (await harness.Claims.UploadAsync(claimId, ClaimDocumentKind.AccidentReport, @"C:\scans\constat.pdf", new MemoryStream(Pdf), Ct)).Value;

        var stored = harness.Storage.Files.Keys.ShouldHaveSingleItem();
        stored.Container.ShouldBe(FileContainers.Inspections);
        stored.Path.ShouldStartWith($"claims/{claimId:N}/");
        var document = (await harness.Claims.GetAsync(claimId, Ct)).Value.Documents.ShouldHaveSingleItem();
        (document.FileName, document.ContentType).ShouldBe(("constat.pdf", "application/pdf"));
        (await harness.Claims.FindFileAsync(documentId, Ct)).Value.Path.ShouldBe(stored.Path);

        (await harness.Claims.RemoveDocumentAsync(claimId, documentId, Ct)).IsSuccess.ShouldBeTrue();
        harness.Db.Events.OfType<ClaimFileDiscarded>().ShouldHaveSingleItem().Path.ShouldBe(stored.Path);
    }

    [Fact]
    public async Task A_closed_claim_is_frozen()
    {
        var harness = new BillingHarness().As(Roles.Manager);
        var claimId = (await harness.Claims.OpenAsync(Form(), Ct)).Value;

        (await harness.Claims.SendToInsurerAsync(claimId, Ct)).IsSuccess.ShouldBeTrue();
        (await harness.Claims.CloseAsync(claimId, Ct)).IsSuccess.ShouldBeTrue();

        (await harness.Claims.AddCostAsync(claimId, new ClaimEntryInput(new DateOnly(2026, 10, 20), "Pièce", 10m), Ct)).Error.ShouldBe(BillingErrors.ClaimClosed);
        (await harness.Claims.UploadAsync(claimId, ClaimDocumentKind.Photo, "p.pdf", new MemoryStream(Pdf), Ct)).Error.ShouldBe(BillingErrors.ClaimClosed);
        harness.Storage.Files.ShouldBeEmpty("the blob of a refused file is deleted");
    }
}
