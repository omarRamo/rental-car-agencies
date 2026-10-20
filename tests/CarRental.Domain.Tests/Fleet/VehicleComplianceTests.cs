using CarRental.Domain.Fleet;
using static CarRental.Domain.Tests.Fleet.FleetTestData;

namespace CarRental.Domain.Tests.Fleet;

public sealed class VehicleComplianceTests
{
    private static readonly DateOnly June30 = new(2027, 6, 30);

    [Fact]
    public void Compliance_ends_at_the_end_of_the_tunis_day_of_the_earliest_mandatory_expiry()
    {
        var until = VehicleCompliance.CompliantUntil(
        [
            (VehicleDocumentType.Insurance, June30),
            (VehicleDocumentType.RoadTax, new DateOnly(2027, 3, 31)),
            (VehicleDocumentType.TechnicalInspection, new DateOnly(2027, 12, 1)),
        ]);

        // End of 31/03/2027 in Tunis (UTC+1) = 31/03/2027 23:00 UTC.
        until.ShouldBe(new DateTimeOffset(2027, 3, 31, 23, 0, 0, TimeSpan.Zero));
        until.ShouldBe(Tunis(2027, 4, 1));
    }

    [Fact]
    public void A_missing_mandatory_document_means_no_compliance_at_all()
    {
        VehicleCompliance.CompliantUntil([(VehicleDocumentType.Insurance, June30), (VehicleDocumentType.RoadTax, June30)]).ShouldBeNull();
        VehicleCompliance.CompliantUntil([]).ShouldBeNull();
    }

    [Fact]
    public void A_mandatory_document_without_expiry_does_not_count()
    {
        VehicleCompliance.CompliantUntil(
        [
            (VehicleDocumentType.Insurance, June30),
            (VehicleDocumentType.RoadTax, null),
            (VehicleDocumentType.TechnicalInspection, June30),
        ]).ShouldBeNull();
    }

    [Fact]
    public void The_registration_card_is_not_mandatory_and_never_shortens_the_compliance()
    {
        var until = VehicleCompliance.CompliantUntil(
        [
            (VehicleDocumentType.Insurance, June30),
            (VehicleDocumentType.RoadTax, June30),
            (VehicleDocumentType.TechnicalInspection, June30),
            (VehicleDocumentType.RegistrationCard, new DateOnly(2020, 1, 1)),
        ]);

        until.ShouldBe(VehicleCompliance.EndOfTunisDay(June30));
        VehicleCompliance.IsMandatory(VehicleDocumentType.RegistrationCard).ShouldBeFalse();
        VehicleCompliance.MandatoryTypes.ShouldBe([VehicleDocumentType.Insurance, VehicleDocumentType.RoadTax, VehicleDocumentType.TechnicalInspection]);
    }

    [Fact]
    public void A_document_expiring_today_is_valid_until_midnight_in_tunis()
    {
        var endOfDay = VehicleCompliance.EndOfTunisDay(new DateOnly(2026, 10, 20));

        endOfDay.ShouldBe(new DateTimeOffset(2026, 10, 20, 23, 0, 0, TimeSpan.Zero));
        (endOfDay > Tunis(2026, 10, 20, 23, 59)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(-1, DeadlineAlertLevel.Expired)]
    [InlineData(-30, DeadlineAlertLevel.Expired)]
    [InlineData(0, DeadlineAlertLevel.Within7Days)]
    [InlineData(7, DeadlineAlertLevel.Within7Days)]
    [InlineData(8, DeadlineAlertLevel.Within15Days)]
    [InlineData(15, DeadlineAlertLevel.Within15Days)]
    [InlineData(16, DeadlineAlertLevel.Within30Days)]
    [InlineData(30, DeadlineAlertLevel.Within30Days)]
    public void Deadline_levels_follow_the_30_15_7_day_thresholds(int daysLeft, DeadlineAlertLevel expected)
    {
        var today = new DateOnly(2026, 10, 20);

        VehicleCompliance.LevelOf(today.AddDays(daysLeft), today).ShouldBe(expected);
    }

    [Fact]
    public void Far_or_unknown_deadlines_have_no_level()
    {
        var today = new DateOnly(2026, 10, 20);

        VehicleCompliance.LevelOf(today.AddDays(31), today).ShouldBeNull();
        VehicleCompliance.LevelOf(null, today).ShouldBeNull();
    }

    [Fact]
    public void Null_documents_are_a_bug()
    {
        Should.Throw<ArgumentNullException>(() => VehicleCompliance.CompliantUntil(null!));
    }
}
