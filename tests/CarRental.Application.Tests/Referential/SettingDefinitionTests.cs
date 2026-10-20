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
        WellKnownSettings.OutboxProcessedRetention.Default.ShouldBe(TimeSpan.FromDays(30));
        WellKnownSettings.OutboxProcessedRetention.DefaultValue.ShouldBe("30.00:00:00");
    }

    [Fact]
    public void The_outbox_retention_keeps_processed_messages_one_day_at_least()
    {
        WellKnownSettings.OutboxProcessedRetention.Validate("1.00:00:00").IsSuccess.ShouldBeTrue();
        WellKnownSettings.OutboxProcessedRetention.Validate("23:59:00").IsFailure.ShouldBeTrue();
        WellKnownSettings.OutboxProcessedRetention.Validate("366.00:00:00").IsFailure.ShouldBeTrue();
        WellKnownSettings.OutboxProcessedRetention.Editor.ShouldBe(new SettingEditor(SettingKind.Duration, "1.00:00:00", "365.00:00:00"));
    }

    [Fact]
    public void Each_factory_declares_the_editor_of_its_kind_with_its_bounds()
    {
        WellKnownSettings.PreparationBuffer.Editor.ShouldBe(new SettingEditor(SettingKind.Duration, "00:00:00", "365.00:00:00"));
        WellKnownSettings.VatRate.Editor.ShouldBe(new SettingEditor(SettingKind.Number, "0", "1"));
        WellKnownSettings.StampDuty.Editor.ShouldBe(new SettingEditor(SettingKind.Amount, "0.000"));
        SettingDefinitions.Boolean("test.flag", true).Editor.ShouldBe(new SettingEditor(SettingKind.Boolean));
        SettingDefinitions.WholeNumber("test.count", 3, 1, 10).Editor.ShouldBe(new SettingEditor(SettingKind.WholeNumber, "1", "10"));
        SettingDefinitions.Text("test.text", "x", maxLength: 40).Editor.ShouldBe(new SettingEditor(SettingKind.Text, MaxLength: 40));
        SettingDefinitions.TimeRange("test.hours", new DailyTimeRange(new TimeOnly(8, 0), new TimeOnly(18, 0))).Editor
            .ShouldBe(new SettingEditor(SettingKind.TimeRange));
    }

    [Fact]
    public void A_custom_definition_gets_the_editor_of_its_value_type()
    {
        static SettingDefinition<T> Custom<T>(T value) => new("test.custom", value, _ => value, _ => "x");

        Custom(TimeSpan.Zero).Editor.Kind.ShouldBe(SettingKind.Duration);
        Custom(1m).Editor.Kind.ShouldBe(SettingKind.Number);
        Custom(1).Editor.Kind.ShouldBe(SettingKind.WholeNumber);
        Custom(Money.Zero).Editor.Kind.ShouldBe(SettingKind.Amount);
        Custom(true).Editor.Kind.ShouldBe(SettingKind.Boolean);
        Custom(new DailyTimeRange(new TimeOnly(8, 0), new TimeOnly(9, 0))).Editor.Kind.ShouldBe(SettingKind.TimeRange);
        Custom(DateOnly.MinValue).Editor.Kind.ShouldBe(SettingKind.Text);
    }

    [Fact]
    public void The_group_is_the_first_segment_of_the_key()
    {
        WellKnownSettings.PreparationBuffer.Group.ShouldBe("fleet");
        WellKnownSettings.DailyContributionExclVat.Group.ShouldBe("billing");
    }

    [Theory]
    [InlineData("nodot")]
    [InlineData("Upper.case")]
    [InlineData("trailing.")]
    [InlineData("1digit.first")]
    public void A_definition_needs_a_storable_key(string key)
    {
        Should.Throw<ArgumentException>(() => SettingDefinitions.Boolean(key, false));
    }

    [Fact]
    public void A_default_value_outside_its_bounds_is_a_declaration_bug()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => SettingDefinitions.Number("test.rate", 2m, 0m, 1m));
        Should.Throw<ArgumentOutOfRangeException>(() => SettingDefinitions.WholeNumber("test.count", 0, 1, 10));
        Should.Throw<ArgumentOutOfRangeException>(() => SettingDefinitions.Duration("test.delay", TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3)));
        Should.Throw<ArgumentOutOfRangeException>(() => SettingDefinitions.Duration("test.delay", TimeSpan.FromDays(400), TimeSpan.Zero, TimeSpan.FromDays(500)));
        Should.Throw<ArgumentOutOfRangeException>(() => SettingDefinitions.Text("test.text", "too long", maxLength: 3));
        Should.Throw<ArgumentOutOfRangeException>(() => SettingDefinitions.TimeRange("test.hours", new DailyTimeRange(new TimeOnly(18, 0), new TimeOnly(8, 0))));
    }

    [Theory]
    [InlineData("Agence TN", true)]
    [InlineData("0123456789", true)]
    [InlineData("", true)]
    [InlineData("0123456789A", false)]
    [InlineData("tab\there", false)]
    public void Texts_are_bounded_and_without_control_characters(string stored, bool valid)
    {
        SettingDefinitions.Text("test.text", "x", maxLength: 10).Validate(stored).IsSuccess.ShouldBe(valid);
    }

    [Theory]
    [InlineData("08:00-18:00", true)]
    [InlineData("00:00-23:59", true)]
    [InlineData("18:00-08:00", false)]
    [InlineData("08:00-08:00", false)]
    [InlineData("8:00-18:00", false)]
    [InlineData("08:00", false)]
    [InlineData("08:00-18:00-20:00", false)]
    public void Time_ranges_start_before_they_end(string stored, bool valid)
    {
        var hours = SettingDefinitions.TimeRange("test.hours", new DailyTimeRange(new TimeOnly(8, 0), new TimeOnly(18, 0)));

        hours.Parse(stored).IsSuccess.ShouldBe(valid);
        hours.DefaultValue.ShouldBe("08:00-18:00");
    }

    [Fact]
    public void A_time_range_contains_its_start_but_not_its_end()
    {
        var range = new DailyTimeRange(new TimeOnly(8, 0), new TimeOnly(18, 0));

        range.Contains(new TimeOnly(8, 0)).ShouldBeTrue();
        range.Contains(new TimeOnly(17, 59)).ShouldBeTrue();
        range.Contains(new TimeOnly(18, 0)).ShouldBeFalse();
        range.ToString().ShouldBe("08:00-18:00");
    }

    [Fact]
    public void Storage_formats_round_trip()
    {
        SettingValues.TryParseDuration(SettingValues.FormatDuration(TimeSpan.FromMinutes(90)), out var duration).ShouldBeTrue();
        duration.ShouldBe(TimeSpan.FromMinutes(90));
        SettingValues.TryParseNumber(SettingValues.FormatNumber(0.075m), out var number).ShouldBeTrue();
        number.ShouldBe(0.075m);
        SettingValues.TryParseWholeNumber(SettingValues.FormatWholeNumber(-3), out var whole).ShouldBeTrue();
        whole.ShouldBe(-3);
        SettingValues.FormatAmount(2.5m).ShouldBe("2.500");
        SettingValues.TryParseBoolean(SettingValues.FormatBoolean(true), out var flag).ShouldBeTrue();
        flag.ShouldBeTrue();
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
