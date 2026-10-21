using CarRental.Application.Billing.Claims;
using CarRental.Application.Billing.Fines;
using CarRental.Application.Billing.Invoicing;
using CarRental.Application.Bookings.Handover;
using CarRental.Application.Common.Files;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Fleet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Billing.BillingWorld;
using static CarRental.Integration.Tests.Bookings.BookingWorld;
using static CarRental.Integration.Tests.Bookings.Handover.HandoverWorld;

namespace CarRental.Integration.Tests.Billing;

/// <summary>
/// Traffic fines and claims on SQL Server and Azurite (ADR-0059): a notice matched with the rental that had the vehicle, its
/// designation form printing the driver's documents (logged), its re-invoicing; a claim with its files in the private
/// container, its costs and reimbursements feeding the vehicle costs of the reports.
/// </summary>
public sealed class FinesAndClaimsIntegrationTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fine_finds_the_driver_who_had_the_vehicle_its_designation_form_is_logged_and_it_is_reinvoiced()
    {
        var file = await ConfirmedAsync(host, Ct);
        await IdentifyAsync(host, file.Agency.AgencyId, Ct);
        await SetAsync(host, file.Agency.AgencyId, "billing.fine_management_fee", "20.000", Ct);
        var agent = FleetData.Actor(Roles.Agent, $"agent-{Guid.NewGuid():N}@tests.local");
        await InspectDepartureAsync(host, agent, file, Ct);
        await SettleAsync(host, agent, file, Ct);
        Ok(await RunAsync<DepartureService, Result<DepartureOutcome>>(host, agent, s => s.FinishAsync(new FinishDepartureRequest(file.BookingId), Ct)));
        var vehicle = (await RunAsync<IFleetQueries, VehicleInfo?>(host, agent, q => q.GetVehicleAsync(file.VehicleId, Ct))).ShouldNotBeNull();

        // The notice names the plate in its own way; the offence happened during the rental.
        var ticket = $"PV{Guid.NewGuid():N}"[..18].ToUpperInvariant();
        var form = new FineForm(file.Agency.AgencyId, ticket, "Police de la circulation", DateTimeOffset.UtcNow, "Avenue de Paris, Tunis", vehicle.PlateNumber.Replace(" ", string.Empty, StringComparison.Ordinal), 60m);
        var fineId = Ok(await RunAsync<FineService, Result<Guid>>(host, agent, s => s.RecordAsync(form, Ct)));
        (await RunAsync<FineService, Result<Guid>>(host, agent, s => s.RecordAsync(form, Ct))).Error.ShouldBe(BillingErrors.FineDuplicate);

        var fine = Ok(await RunAsync<FineService, Result<FineDetail>>(host, agent, s => s.GetAsync(fineId, Ct)));
        (fine.Match, fine.BookingId, fine.DriverPersonId, fine.VehicleId).ShouldBe((FineMatch.Matched, file.BookingId, file.DriverId, file.VehicleId));
        fine.Status.ShouldBe(FineStatus.Recorded);
        fine.Actions.CanProduceDesignation.ShouldBeTrue();
        fine.Actions.ManagementFee.ShouldBe(Money.Tnd(20m));

        // The designation form prints the driver's documents in full: only with customers.documents.read, always logged.
        (await RunAsync<FineService, Result<FineDesignationFile>>(host, Accountant, s => s.ProduceDesignationFormAsync(fineId, Ct))).Error.ShouldBe(BillingErrors.Forbidden);
        var designation = Ok(await RunAsync<FineService, Result<FineDesignationFile>>(host, agent, s => s.ProduceDesignationFormAsync(fineId, Ct)));
        var text = TextOf(designation.Content);
        text.ShouldContain(ticket);
        text.ShouldContain(file.Number);
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            var reason = $"fine:{ticket}";
            var accesses = await db.Set<DocumentAccessLog>().AsNoTracking().Where(a => a.Access == FineService.DesignationAccess && a.Reason == reason).ToListAsync(Ct);
            accesses.Count.ShouldBe(2, "the identity card and the driving licence of the driver");
            accesses.ShouldAllBe(a => a.UserName == agent.UserName);
            var fineKey = fineId.ToString("D");
            (await db.Set<AuditLog>().AsNoTracking().CountAsync(a => a.Action == FineService.DesignationAuditAction && a.EntityId == fineKey, Ct)).ShouldBe(1);
        }

        Ok(await RunAsync<FineService, Result>(host, agent, s => s.MarkDesignatedAsync(fineId, Ct)));
        var reinvoiced = Ok(await RunAsync<FineService, Result<IssuedDocument>>(host, agent, s => s.ReinvoiceAsync(fineId, Ct)));
        Ok(await RunAsync<FineService, Result<IssuedDocument>>(host, agent, s => s.ReinvoiceAsync(fineId, Ct))).ShouldBe(reinvoiced with { AlreadyIssued = true });

        var invoice = await DocumentAsync<Invoice>(host, reinvoiced.Id, i => i.PdfPath is not null, Ct);
        (invoice.Kind, invoice.FineId, invoice.BookingId).ShouldBe((InvoiceKind.FineReinvoicing, fineId, file.BookingId));
        invoice.TotalInclVat.ShouldBe(Money.Tnd(80m), "the fine and the management fee");
        await using (var scope = host.CreateScope())
        {
            var lines = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Invoice>().AsNoTracking().Where(i => i.Id == invoice.Id).SelectMany(i => i.Lines).ToListAsync(Ct);
            lines.Select(l => (l.Nature, l.InclVat)).ShouldBe([(InvoiceLineNature.FineReinvoicing, Money.Tnd(60m)), (InvoiceLineNature.ManagementFee, Money.Tnd(20m))], ignoreOrder: true);
        }

        var after = Ok(await RunAsync<FineService, Result<FineDetail>>(host, agent, s => s.GetAsync(fineId, Ct)));
        (after.Status, after.InvoiceId, after.InvoiceNumber).ShouldBe((FineStatus.Reinvoiced, invoice.Id, invoice.Number));
        Ok(await RunAsync<FineService, Result>(host, agent, s => s.MarkPaidAsync(fineId, Ct)));
        Ok(await RunAsync<FineService, Result>(host, agent, s => s.CloseAsync(fineId, "Réglée par le client", Ct)));
        Ok(await RunAsync<FineService, Result<FineDetail>>(host, agent, s => s.GetAsync(fineId, Ct))).Status.ShouldBe(FineStatus.Closed);
    }

    [Fact]
    public async Task A_notice_for_a_plate_unknown_to_the_fleet_waits_for_an_agent()
    {
        var agency = await AgencyAsync(host, Ct);
        var form = new FineForm(agency.AgencyId, $"PV{Guid.NewGuid():N}"[..18], "Garde nationale", DateTimeOffset.UtcNow.AddDays(-2), "Route de Bizerte", "999 TU 9999", 40m);

        var fineId = Ok(await RunAsync<FineService, Result<Guid>>(host, BillingAgent, s => s.RecordAsync(form, Ct)));

        var fine = Ok(await RunAsync<FineService, Result<FineDetail>>(host, BillingAgent, s => s.GetAsync(fineId, Ct)));
        (fine.Match, fine.BookingId, fine.VehicleId).ShouldBe((FineMatch.UnknownVehicle, null, null));
        fine.Actions.CanProduceDesignation.ShouldBeFalse();
        fine.Actions.CanReinvoice.ShouldBeFalse();
        var waiting = Ok(await RunAsync<FineService, Result<CarRental.Application.Common.Paging.PagedResult<FineListItem>>>(
            host,
            BillingAgent,
            s => s.ListAsync(new FineListFilter(agency.AgencyId, OnlyToAssign: true), new CarRental.Application.Common.Paging.PageRequest(1, 25), Ct)));
        waiting.Items.ShouldHaveSingleItem().Id.ShouldBe(fineId);
    }

    [Fact]
    public async Task A_claim_keeps_its_files_privately_and_its_net_cost_feeds_the_vehicle_costs_of_the_reports()
    {
        var agency = await AgencyAsync(host, Ct);
        var vehicleId = agency.Agency.EcmrVehicles[0];
        var today = TunisTime.Today(TimeProvider.System);
        var claimId = Ok(await RunAsync<ClaimService, Result<Guid>>(
            host,
            BillingAgent,
            s => s.OpenAsync(new ClaimForm(agency.AgencyId, vehicleId, null, DateTimeOffset.UtcNow.AddHours(-3), "Rocade de Tunis", "Choc arrière à l'arrêt", "Assureur de test", "SIN-0042", 500m), Ct)));

        Ok(await RunAsync<ClaimService, Result>(host, BillingAgent, s => s.AddCostAsync(claimId, new ClaimEntryInput(today, "Réparation carrosserie", 1_200m, "DEVIS-1"), Ct)));
        Ok(await RunAsync<ClaimService, Result>(host, BillingAgent, s => s.AddReimbursementAsync(claimId, new ClaimEntryInput(today, "Remboursement de l'assureur", 700m), Ct)));
        var photoId = Ok(await RunAsync<ClaimService, Result<Guid>>(host, BillingAgent, s => s.UploadAsync(claimId, ClaimDocumentKind.Photo, @"C:\photos\arriere.jpg", new MemoryStream(Jpeg), Ct)));
        (await RunAsync<ClaimService, Result<Guid>>(host, BillingAgent, s => s.UploadAsync(claimId, ClaimDocumentKind.Other, "outil.exe", new MemoryStream("MZ-not-a-picture"u8.ToArray()), Ct)))
            .Error.ShouldBe(BillingErrors.ClaimFileInvalid);
        (await RunAsync<ClaimService, Result<Guid>>(host, FleetData.Actor(Roles.Preparer), s => s.OpenAsync(new ClaimForm(agency.AgencyId, vehicleId, null, DateTimeOffset.UtcNow, null, "Rayure", null, null, 0m), Ct)))
            .Error.ShouldBe(BillingErrors.Forbidden);

        var claim = Ok(await RunAsync<ClaimService, Result<ClaimDetail>>(host, BillingAgent, s => s.GetAsync(claimId, Ct)));
        (claim.TotalCosts, claim.TotalReimbursements, claim.NetCost, claim.Franchise).ShouldBe((Money.Tnd(1_200m), Money.Tnd(700m), Money.Tnd(500m), Money.Tnd(500m)));
        var document = claim.Documents.ShouldHaveSingleItem();
        (document.Id, document.Kind, document.FileName, document.ContentType).ShouldBe((photoId, ClaimDocumentKind.Photo, "arriere.jpg", "image/jpeg"));

        var stored = Ok(await RunAsync<ClaimService, Result<ClaimFile>>(host, BillingAgent, s => s.FindFileAsync(photoId, Ct)));
        stored.Container.ShouldBe(FileContainers.Inspections);
        stored.Path.ShouldStartWith($"claims/{claimId:N}/");
        (await ReadAsync(host, stored.Container, stored.Path, Ct)).ShouldBe(Jpeg);

        await using (var scope = host.CreateScope())
        {
            var source = scope.ServiceProvider.GetServices<IVehicleCostsSource>().Single(s => s.Name == "claims");
            var costs = await source.GetCostsAsync(agency.AgencyId, today, today, Ct);
            costs.Select(c => c.Amount).ShouldBe([Money.Tnd(1_200m), Money.Tnd(-700m)], ignoreOrder: true);
            costs.ShouldAllBe(c => c.VehicleId == vehicleId && c.From == today && c.To == today);
        }

        Ok(await RunAsync<ClaimService, Result>(host, BillingAgent, s => s.SendToInsurerAsync(claimId, Ct)));
        Ok(await RunAsync<ClaimService, Result>(host, BillingAgent, s => s.CloseAsync(claimId, Ct)));
        (await RunAsync<ClaimService, Result>(host, BillingAgent, s => s.AddCostAsync(claimId, new ClaimEntryInput(today, "Encore", 10m), Ct))).Error.ShouldBe(BillingErrors.ClaimClosed);
    }
}
