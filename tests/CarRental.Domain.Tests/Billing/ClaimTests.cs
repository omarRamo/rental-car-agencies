using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Billing.BillingTestData;

namespace CarRental.Domain.Tests.Billing;

/// <summary>Claims: circumstances, insurer and franchise, costs and reimbursements (the net cost), files, statuses.</summary>
public sealed class ClaimTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid VehicleId = Guid.CreateVersion7();

    private static ClaimDetails Details() => new(Now.AddDays(-2), "Rond-point de la Charguia", "Accrochage à l'arrière, tiers identifié", "STAR Assurances", "SIN-55-2026", 500m);

    private static Claim Opened() => Claim.Open(AgencyId, VehicleId, Guid.CreateVersion7(), "LOC-2026-000077", Details(), Accountant, Now).Value;

    private static ClaimFileInput File(string name = "constat.pdf") =>
        new(ClaimDocumentKind.AccidentReport, name, $"claims/{Guid.CreateVersion7():N}/{Guid.CreateVersion7():N}.pdf", "application/pdf", 2_048, new string('b', Claim.HashLength));

    [Fact]
    public void A_claim_is_opened_for_a_vehicle_with_its_circumstances_insurer_and_franchise()
    {
        var claim = Opened();

        (claim.Status, claim.Insurer, claim.InsurerClaimNumber, claim.Franchise).ShouldBe((ClaimStatus.Open, "STAR Assurances", "SIN-55-2026", Money.Tnd(500m)));
        claim.BookingNumber.ShouldBe("LOC-2026-000077");
        claim.NetCost.ShouldBe(Money.Zero);
        claim.DomainEvents.OfType<ClaimOpened>().ShouldHaveSingleItem().VehicleId.ShouldBe(VehicleId);

        Claim.Open(AgencyId, Guid.Empty, null, null, Details(), Accountant, Now).Error.ShouldBe(BillingErrors.ClaimVehicleRequired);
        Claim.Open(AgencyId, VehicleId, null, null, Details() with { Description = "x" }, Accountant, Now).Error.ShouldBe(BillingErrors.ClaimDescriptionRequired);
        Claim.Open(AgencyId, VehicleId, null, null, Details() with { AccidentAt = Now.AddHours(1) }, Accountant, Now).Error.ShouldBe(BillingErrors.ClaimAccidentInFuture);
        Claim.Open(AgencyId, VehicleId, null, null, Details() with { Franchise = -1m }, Accountant, Now).Error.ShouldBe(BillingErrors.ClaimAmountInvalid);
    }

    [Fact]
    public void The_net_cost_is_the_costs_minus_the_reimbursements()
    {
        var claim = Opened();

        var repair = claim.AddCost(new ClaimEntryInput(new DateOnly(2026, 10, 15), "Réparation carrosserie", 1_800m, "Facture G-12"), Accountant, Now).Value;
        claim.AddCost(new ClaimEntryInput(new DateOnly(2026, 10, 14), "Remorquage", 120m), Accountant, Now).IsSuccess.ShouldBeTrue();
        claim.AddReimbursement(new ClaimEntryInput(new DateOnly(2026, 10, 30), "Indemnité de l'assureur", 1_300m, "VIR-889"), Accountant, Now).IsSuccess.ShouldBeTrue();

        (claim.TotalCosts, claim.TotalReimbursements, claim.NetCost).ShouldBe((Money.Tnd(1_920m), Money.Tnd(1_300m), Money.Tnd(620m)));
        claim.Costs.Select(c => c.Label).ShouldBe(["Remorquage", "Réparation carrosserie"], "by date");

        claim.RemoveCost(repair, Accountant, Now).IsSuccess.ShouldBeTrue();
        claim.NetCost.ShouldBe(Money.Tnd(-1_180m));
        claim.RemoveCost(repair, Accountant, Now).Error.ShouldBe(BillingErrors.ClaimEntryNotFound);
        claim.AddCost(new ClaimEntryInput(new DateOnly(2026, 10, 15), " ", 10m), Accountant, Now).Error.ShouldBe(BillingErrors.ClaimLabelRequired);
        claim.AddCost(new ClaimEntryInput(new DateOnly(2026, 10, 15), "Pièce", 0m), Accountant, Now).Error.ShouldBe(BillingErrors.ClaimAmountInvalid);
        claim.Revision.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void The_costs_reimbursements_and_files_of_a_claim_belong_to_its_agency()
    {
        var claim = Opened();

        claim.AddCost(new ClaimEntryInput(new DateOnly(2026, 10, 15), "Réparation", 800m), Accountant, Now).IsSuccess.ShouldBeTrue();
        claim.AddReimbursement(new ClaimEntryInput(new DateOnly(2026, 10, 30), "Indemnité", 300m), Accountant, Now).IsSuccess.ShouldBeTrue();
        claim.AddDocument(File(), Accountant, Now).IsSuccess.ShouldBeTrue();

        claim.AgencyId.ShouldBe(AgencyId);
        claim.Costs.ShouldAllBe(c => c.AgencyId == AgencyId);
        claim.Reimbursements.ShouldAllBe(r => r.AgencyId == AgencyId);
        claim.Documents.ShouldAllBe(d => d.AgencyId == AgencyId);
    }

    [Fact]
    public void A_removed_file_is_discarded_after_the_commit()
    {
        var claim = Opened();
        var file = File();
        var documentId = claim.AddDocument(file, Accountant, Now).Value;

        claim.Documents.ShouldHaveSingleItem().FileName.ShouldBe("constat.pdf");
        claim.AddDocument(file with { ContentHash = "short" }, Accountant, Now).Error.ShouldBe(BillingErrors.ClaimFileInvalid);
        claim.RemoveDocument(documentId, Accountant, Now).IsSuccess.ShouldBeTrue();

        claim.Documents.ShouldBeEmpty();
        claim.DomainEvents.OfType<ClaimFileDiscarded>().ShouldHaveSingleItem().Path.ShouldBe(file.StoragePath);
    }

    [Fact]
    public void A_claim_goes_to_the_insurer_comes_back_and_once_closed_never_changes()
    {
        var claim = Opened();

        claim.Reopen(Accountant, Now).Error.ShouldBe(BillingErrors.ClaimInvalidTransition);
        claim.SendToInsurer(Accountant, Now).IsSuccess.ShouldBeTrue();
        claim.SendToInsurer(Accountant, Now).Error.ShouldBe(BillingErrors.ClaimInvalidTransition);
        claim.Reopen(Accountant, Now).IsSuccess.ShouldBeTrue();
        claim.Close(Accountant, Now).IsSuccess.ShouldBeTrue();

        claim.ClosedAtUtc.ShouldBe(Now);
        claim.Close(Accountant, Now).Error.ShouldBe(BillingErrors.ClaimInvalidTransition);
        claim.UpdateDetails(Details(), Accountant, Now).Error.ShouldBe(BillingErrors.ClaimClosed);
        claim.AddCost(new ClaimEntryInput(new DateOnly(2026, 10, 15), "Pièce", 10m), Accountant, Now).Error.ShouldBe(BillingErrors.ClaimClosed);
        claim.AddDocument(File(), Accountant, Now).Error.ShouldBe(BillingErrors.ClaimClosed);
        claim.DomainEvents.OfType<ClaimStatusChanged>().Select(e => e.Status).ShouldBe([ClaimStatus.AtInsurer, ClaimStatus.Open, ClaimStatus.Closed]);
    }
}
