using CarRental.Application.Referential.Contracts;

namespace CarRental.Testing;

/// <summary>
/// Settings declared only by the test hosts, so that tests can change platform values without touching the settings that
/// other tests read. They have no label: the settings screen shows their key (the documented fallback).
/// </summary>
public static class TestSettings
{
    public static readonly SettingDefinition<TimeSpan> Delay = SettingDefinitions.Duration("tests.delay", TimeSpan.FromHours(2));

    public static readonly SettingDefinition<string> Motto = SettingDefinitions.Text("tests.motto", "Bienvenue", maxLength: 60);

    public static readonly SettingDefinition<DailyTimeRange> OpeningHours =
        SettingDefinitions.TimeRange("tests.opening_hours", new DailyTimeRange(new TimeOnly(8, 0), new TimeOnly(18, 0)));

    public static IReadOnlyList<SettingDefinition> All { get; } = [Delay, Motto, OpeningHours];
}
