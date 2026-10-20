using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;

namespace CarRental.Application.Tests.Referential;

public sealed class SettingDefinitionTests
{
    [Fact]
    public void Well_known_settings_have_the_specified_defaults()
    {
        WellKnownSettings.BookingHoldDuration.Default.ShouldBe(TimeSpan.FromHours(24));
        WellKnownSettings.BookingHoldDuration.DefaultValue.ShouldBe("1.00:00:00");
        WellKnownSettings.PreparationBuffer.Default.ShouldBe(TimeSpan.FromHours(2));
        WellKnownSettings.LateReturnTolerance.Default.ShouldBe(TimeSpan.FromMinutes(59));
        WellKnownSettings.LateReturnTolerance.DefaultValue.ShouldBe("00:59:00");
        WellKnownSettings.VatRate.Default.ShouldBe(0.19m);
        WellKnownSettings.DailyContributionExclVat.Default.ShouldBe(Money.Tnd(2m));
        WellKnownSettings.DailyContributionExclVat.DefaultValue.ShouldBe("2.000");
        WellKnownSettings.StampDuty.Default.ShouldBe(Money.Zero);
        WellKnownSettings.StampDuty.DefaultValue.ShouldBe("0.000");
    }

    [Fact]
    public void Well_known_keys_are_unique_and_storable()
    {
        WellKnownSettings.All.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count().ShouldBe(WellKnownSettings.All.Count);
        foreach (var definition in WellKnownSettings.All)
        {
            Setting.Create(definition.Key, definition.DefaultValue).IsSuccess.ShouldBeTrue(definition.Key);
            definition.Validate(definition.DefaultValue).IsSuccess.ShouldBeTrue(definition.Key);
            definition.ToString().ShouldBe(definition.Key);
        }
    }

    [Theory]
    [InlineData("02:00:00", 120)]
    [InlineData("1.00:00:00", 1440)]
    [InlineData("00:00:00", 0)]
    public void Durations_use_the_invariant_constant_format(string stored, int expectedMinutes)
    {
        WellKnownSettings.PreparationBuffer.Parse(stored).Value.ShouldBe(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Theory]
    [InlineData("24:00:00")]
    [InlineData("-01:00:00")]
    [InlineData("2 h")]
    [InlineData("400.00:00:00")]
    [InlineData(null)]
    public void Invalid_durations_are_rejected(string? stored)
    {
        WellKnownSettings.PreparationBuffer.Parse(stored).Error.Code.ShouldBe("referential.setting_value_invalid");
        WellKnownSettings.PreparationBuffer.Validate(stored).IsFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData("0.19", true)]
    [InlineData("0", true)]
    [InlineData("1", true)]
    [InlineData("1.01", false)]
    [InlineData("-0.1", false)]
    [InlineData("0,19", false)]
    [InlineData("abc", false)]
    public void Rates_are_bounded_and_invariant(string stored, bool valid)
    {
        WellKnownSettings.VatRate.Parse(stored).IsSuccess.ShouldBe(valid);
    }

    [Theory]
    [InlineData("2.000", true)]
    [InlineData("2", true)]
    [InlineData("2.5", true)]
    [InlineData("2.0005", false)]
    [InlineData("-1", false)]
    [InlineData("deux", false)]
    public void Amounts_are_positive_millimes(string stored, bool valid)
    {
        WellKnownSettings.DailyContributionExclVat.Parse(stored).IsSuccess.ShouldBe(valid);
    }

    [Fact]
    public void Formatting_round_trips()
    {
        WellKnownSettings.DailyContributionExclVat.Format(Money.Tnd(2.5m)).ShouldBe("2.500");
        WellKnownSettings.VatRate.Format(0.07m).ShouldBe("0.07");
        WellKnownSettings.BookingHoldDuration.Format(TimeSpan.FromHours(36)).ShouldBe("1.12:00:00");
    }

    [Fact]
    public void Boolean_and_whole_number_settings()
    {
        var flag = SettingDefinitions.Boolean("test.flag", defaultValue: true);
        flag.DefaultValue.ShouldBe("true");
        flag.Parse("false").Value.ShouldBeFalse();
        flag.Parse("yes").IsFailure.ShouldBeTrue();

        var count = SettingDefinitions.WholeNumber("test.count", 3, minimum: 1, maximum: 10);
        count.Parse("10").Value.ShouldBe(10);
        count.Parse("11").IsFailure.ShouldBeTrue();
        count.Parse("1.5").IsFailure.ShouldBeTrue();
        count.Format(7).ShouldBe("7");
    }
}
