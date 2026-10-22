using CarRental.Application.Fleet.Deadlines;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;

namespace CarRental.Application.Tests.Fleet;

public sealed class DeadlineAlertJobTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);
    private readonly FleetTestBed _bed = new();

    public DeadlineAlertJobTests()
    {
        _bed.Identity.Users.Add((new UserContactInfo(Guid.CreateVersion7(), "Gérant Démo", "gerant@demo.local"), Roles.Manager));
        _bed.Identity.Users.Add((new UserContactInfo(Guid.CreateVersion7(), "Second Gérant", "gerant2@demo.local"), Roles.Manager));
        _bed.Identity.Users.Add((new UserContactInfo(Guid.CreateVersion7(), "Agent Démo", "agent@demo.local"), Roles.Agent));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_job_runs_every_day_at_seven_in_tunis()
    {
        DeadlineAlertJob.JobId.ShouldBe("fleet.deadline_alerts");
        DeadlineAlertJob.CronExpression.ShouldBe("0 7 * * *");
    }

    [Fact]
    public async Task One_email_per_manager_is_sent_for_a_document_reaching_a_threshold()
    {
        var vehicle = _bed.AddVehicle(_bed.AddCategory(), documentsExpireOn: new DateOnly(2027, 6, 30));
        vehicle.RecordDocument(new VehicleDocumentDetails(VehicleDocumentType.Insurance, "POL-1", null, Today.AddDays(6)), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();

        await _bed.AlertJob().ExecuteAsync(Ct);

        _bed.Emails.Messages.Select(m => m.To).ShouldBe(["gerant@demo.local", "gerant2@demo.local"], ignoreOrder: true);
        _bed.Emails.Messages.ShouldAllBe(m => m.Subject.Contains("215 TU 4512") && m.Subject.Contains("Insurance") && m.Subject.Contains("Within7Days"));
        var alert = _bed.Db.Fake<DeadlineAlert>().Items.ShouldHaveSingleItem();
        alert.DocumentId.ShouldBe(vehicle.CurrentDocument(VehicleDocumentType.Insurance)!.Id);
        alert.Level.ShouldBe(DeadlineAlertLevel.Within7Days);
        alert.RecipientCount.ShouldBe(2);
        alert.SentAtUtc.ShouldBe(FleetTestBed.Now);
    }

    [Fact]
    public async Task The_managers_of_each_agency_hear_of_the_vehicles_of_their_agency_only()
    {
        _bed.Identity.Users.Clear();
        var sousse = Guid.CreateVersion7();
        _bed.Referential.Agencies.Add(new AgencyInfo(sousse, "Agence Sousse", "Sousse", null, null, IsActive: true));
        _bed.Identity.AgencyUsers.Add((_bed.AgencyId, new UserContactInfo(Guid.CreateVersion7(), "Gérant Test", "gerant.test@demo.local"), Roles.Manager));
        _bed.Identity.AgencyUsers.Add((sousse, new UserContactInfo(Guid.CreateVersion7(), "Gérant Sousse", "gerant.sousse@demo.local"), Roles.Manager));
        var own = _bed.AddVehicle(_bed.AddCategory(), "215 TU 1", documentsExpireOn: new DateOnly(2027, 6, 30));
        own.RecordDocument(new VehicleDocumentDetails(VehicleDocumentType.Insurance, "POL-1", null, Today.AddDays(6)), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        var other = _bed.AddVehicle(_bed.AddCategory(agencyId: sousse), "215 TU 2", documentsExpireOn: new DateOnly(2027, 6, 30));
        other.RecordDocument(new VehicleDocumentDetails(VehicleDocumentType.RoadTax, "VIG-1", null, Today.AddDays(12)), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();

        await _bed.AlertJob().ExecuteAsync(Ct);

        _bed.Emails.Messages.Select(m => (m.To, m.Subject)).ShouldBe(
            [
                ("gerant.test@demo.local", "Agence Test: 215 TU 1 Insurance Within7Days"),
                ("gerant.sousse@demo.local", "Agence Sousse: 215 TU 2 RoadTax Within15Days"),
            ],
            ignoreOrder: true);
        _bed.Db.Fake<DeadlineAlert>().Items.Select(a => (a.VehicleId, a.AgencyId, a.RecipientCount)).ShouldBe(
            [(own.Id, _bed.AgencyId, 1), (other.Id, sousse, 1)],
            ignoreOrder: true);
    }

    [Fact]
    public async Task A_second_run_the_same_day_sends_nothing()
    {
        var vehicle = _bed.AddVehicle(_bed.AddCategory(), documentsExpireOn: Today.AddDays(20));

        await _bed.AlertJob().ExecuteAsync(Ct);
        var first = _bed.Emails.Messages.Count;
        await _bed.AlertJob().ExecuteAsync(Ct);

        first.ShouldBe(3 * 2);
        _bed.Emails.Messages.Count.ShouldBe(first);
        _bed.Db.Fake<DeadlineAlert>().Items.Count.ShouldBe(3);
        _bed.Db.Fake<DeadlineAlert>().Items.ShouldAllBe(a => a.Level == DeadlineAlertLevel.Within30Days && a.VehicleId == vehicle.Id);
    }

    [Fact]
    public async Task Each_threshold_is_sent_once_as_the_deadline_approaches_and_then_expires()
    {
        var vehicle = _bed.AddVehicle(_bed.AddCategory(), documentsExpireOn: new DateOnly(2027, 6, 30));
        vehicle.RecordDocument(new VehicleDocumentDetails(VehicleDocumentType.TechnicalInspection, "VT-1", null, Today.AddDays(30)), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();

        for (var day = 0; day <= 35; day++)
        {
            await _bed.AlertJob().ExecuteAsync(Ct);
            _bed.Time.Advance(TimeSpan.FromDays(1));
        }

        _bed.Db.Fake<DeadlineAlert>().Items.Select(a => a.Level)
            .ShouldBe([DeadlineAlertLevel.Within30Days, DeadlineAlertLevel.Within15Days, DeadlineAlertLevel.Within7Days, DeadlineAlertLevel.Expired]);
        _bed.Emails.Messages.Count.ShouldBe(4 * 2);
    }

    [Fact]
    public async Task A_renewal_is_a_new_document_and_starts_afresh()
    {
        var vehicle = _bed.AddVehicle(_bed.AddCategory(), documentsExpireOn: new DateOnly(2027, 6, 30));
        vehicle.RecordDocument(new VehicleDocumentDetails(VehicleDocumentType.RoadTax, "VIG-1", null, Today.AddDays(10)), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        await _bed.AlertJob().ExecuteAsync(Ct);

        vehicle.RecordDocument(new VehicleDocumentDetails(VehicleDocumentType.RoadTax, "VIG-2", null, Today.AddDays(25)), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        await _bed.AlertJob().ExecuteAsync(Ct);

        _bed.Db.Fake<DeadlineAlert>().Items.Select(a => (a.Level, a.DocumentId))
            .ShouldBe([(DeadlineAlertLevel.Within15Days, vehicle.Documents.First(d => d.Number == "VIG-1").Id), (DeadlineAlertLevel.Within30Days, vehicle.CurrentDocument(VehicleDocumentType.RoadTax)!.Id)]);
    }

    [Fact]
    public async Task Far_deadlines_documents_without_expiry_and_sold_vehicles_are_ignored()
    {
        var category = _bed.AddCategory();
        _bed.AddVehicle(category, "215 TU 1", documentsExpireOn: Today.AddDays(45));
        var withCard = _bed.AddVehicle(category, "215 TU 2", documentsExpireOn: Today.AddDays(45));
        withCard.RecordDocument(new VehicleDocumentDetails(VehicleDocumentType.RegistrationCard, "CG-1", null, null), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        var sold = _bed.AddVehicle(category, "215 TU 3", documentsExpireOn: Today.AddDays(3));
        sold.Sell(FleetTestBed.Now, Money.Tnd(1m), null, FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();

        await _bed.AlertJob().ExecuteAsync(Ct);

        _bed.Emails.Messages.ShouldBeEmpty();
        _bed.Db.Fake<DeadlineAlert>().Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_an_active_manager_nothing_is_recorded_so_the_next_run_retries()
    {
        _bed.Identity.Users.RemoveAll(u => u.Role == Roles.Manager);
        _bed.AddVehicle(_bed.AddCategory(), documentsExpireOn: Today.AddDays(2));

        await _bed.AlertJob().ExecuteAsync(Ct);

        _bed.Emails.Messages.ShouldBeEmpty();
        _bed.Db.Fake<DeadlineAlert>().Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_already_expired_document_is_alerted_once()
    {
        _bed.AddVehicle(_bed.AddCategory(), documentsExpireOn: Today.AddDays(-3));

        await _bed.AlertJob().ExecuteAsync(Ct);
        await _bed.AlertJob().ExecuteAsync(Ct);

        _bed.Db.Fake<DeadlineAlert>().Items.Count.ShouldBe(3);
        _bed.Db.Fake<DeadlineAlert>().Items.ShouldAllBe(a => a.Level == DeadlineAlertLevel.Expired);
        _bed.Emails.Messages.Count.ShouldBe(6);
    }
}
