using CarRental.Domain.Referential;

namespace CarRental.Domain.Tests.Referential;

public sealed class SettingTests
{
    [Fact]
    public void Create_raises_a_change_event_without_previous_value()
    {
        var setting = Setting.Create("bookings.hold_duration", "1.00:00:00").Value;

        setting.Key.ShouldBe("bookings.hold_duration");
        setting.Value.ShouldBe("1.00:00:00");
        setting.AgencyId.ShouldBeNull();
        setting.DomainEvents.ShouldHaveSingleItem()
            .ShouldBe(new SettingChanged(setting.Id, "bookings.hold_duration", null, null, "1.00:00:00"));
    }

    [Theory]
    [InlineData("HoldDuration")]
    [InlineData("bookings")]
    [InlineData("bookings.Hold")]
    [InlineData("")]
    public void Keys_are_dotted_lower_case(string key)
    {
        Setting.Create(key, "x").Error.ShouldBe(ReferentialErrors.SettingKeyInvalid);
    }

    [Fact]
    public void Values_are_bounded_and_agency_cannot_be_empty()
    {
        Setting.Create("a.b", new string('x', Setting.ValueMaxLength + 1)).Error.ShouldBe(ReferentialErrors.SettingValueInvalid);
        Setting.Create("a.b", null!).Error.ShouldBe(ReferentialErrors.SettingValueInvalid);
        Setting.Create("a.b", "x", Guid.Empty).Error.ShouldBe(ReferentialErrors.AgencyRequired);
        Setting.Create("a.b", string.Empty).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void ChangeValue_records_the_previous_value()
    {
        var agencyId = Guid.CreateVersion7();
        var setting = Setting.Create("billing.vat_rate", "0.19", agencyId).Value;
        setting.DequeueDomainEvents();

        setting.ChangeValue("0.20").IsSuccess.ShouldBeTrue();

        setting.Value.ShouldBe("0.20");
        setting.DomainEvents.ShouldHaveSingleItem()
            .ShouldBe(new SettingChanged(setting.Id, "billing.vat_rate", agencyId, "0.19", "0.20"));
    }

    [Fact]
    public void ChangeValue_to_the_same_value_does_nothing()
    {
        var setting = Setting.Create("billing.vat_rate", "0.19").Value;
        setting.DequeueDomainEvents();

        setting.ChangeValue("0.19").IsSuccess.ShouldBeTrue();
        setting.DomainEvents.ShouldBeEmpty();
        setting.ChangeValue(null!).Error.ShouldBe(ReferentialErrors.SettingValueInvalid);
    }
}
