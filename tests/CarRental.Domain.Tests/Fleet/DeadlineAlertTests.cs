using CarRental.Domain.Fleet;
using static CarRental.Domain.Tests.Fleet.FleetTestData;

namespace CarRental.Domain.Tests.Fleet;

public sealed class DeadlineAlertTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    [Fact]
    public void The_current_level_is_sent_once()
    {
        var expiresOn = Today.AddDays(25);

        DeadlineAlert.LevelToSend(expiresOn, Today, []).ShouldBe(DeadlineAlertLevel.Within30Days);
        DeadlineAlert.LevelToSend(expiresOn, Today, [DeadlineAlertLevel.Within30Days]).ShouldBeNull();
    }

    [Fact]
    public void Each_more_urgent_level_is_sent_in_turn()
    {
        var expiresOn = Today.AddDays(30);
        var sent = new List<DeadlineAlertLevel>();

        foreach (var day in Enumerable.Range(0, 45))
        {
            if (DeadlineAlert.LevelToSend(expiresOn, Today.AddDays(day), sent) is { } level)
            {
                sent.Add(level);
            }
        }

        sent.ShouldBe([DeadlineAlertLevel.Within30Days, DeadlineAlertLevel.Within15Days, DeadlineAlertLevel.Within7Days, DeadlineAlertLevel.Expired]);
    }

    [Fact]
    public void Levels_skipped_while_the_job_did_not_run_are_not_sent_afterwards()
    {
        var expiresOn = Today.AddDays(5);

        DeadlineAlert.LevelToSend(expiresOn, Today, []).ShouldBe(DeadlineAlertLevel.Within7Days);
        DeadlineAlert.LevelToSend(expiresOn, Today, [DeadlineAlertLevel.Within7Days]).ShouldBeNull();
        DeadlineAlert.LevelToSend(expiresOn, Today.AddDays(6), [DeadlineAlertLevel.Within7Days]).ShouldBe(DeadlineAlertLevel.Expired);
    }

    [Fact]
    public void Far_deadlines_are_not_alerted()
    {
        DeadlineAlert.LevelToSend(Today.AddDays(31), Today, []).ShouldBeNull();
    }

    [Fact]
    public void An_alert_records_the_document_and_the_level()
    {
        var vehicle = NewVehicle();
        var insurance = vehicle.CurrentDocument(VehicleDocumentType.Insurance)!;
        var sentAt = new DateTimeOffset(2026, 10, 20, 7, 0, 0, TimeSpan.FromHours(1));

        var alert = DeadlineAlert.Record(insurance, AgencyId, DeadlineAlertLevel.Within7Days, sentAt, 2);

        alert.DocumentId.ShouldBe(insurance.Id);
        alert.VehicleId.ShouldBe(vehicle.Id);
        alert.AgencyId.ShouldBe(AgencyId);
        alert.DocumentType.ShouldBe(VehicleDocumentType.Insurance);
        alert.ExpiresOn.ShouldBe(insurance.ExpiresOn!.Value);
        alert.Level.ShouldBe(DeadlineAlertLevel.Within7Days);
        alert.SentAtUtc.Offset.ShouldBe(TimeSpan.Zero);
        alert.RecipientCount.ShouldBe(2);
        Should.Throw<ArgumentOutOfRangeException>(() => DeadlineAlert.Record(insurance, AgencyId, DeadlineAlertLevel.Expired, sentAt, -1));
    }

    [Fact]
    public void Documents_without_expiry_are_never_alerted()
    {
        var vehicle = NewVehicle();
        var card = vehicle.RecordDocument(new VehicleDocumentDetails(VehicleDocumentType.RegistrationCard, "CG-1", null, null), Stamp()).Value;

        Should.Throw<ArgumentException>(() => DeadlineAlert.Record(card, AgencyId, DeadlineAlertLevel.Expired, Now, 1));
    }
}
