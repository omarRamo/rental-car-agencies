using System.Globalization;
using CarRental.Domain.Common;
using CarRental.Web.Components.Admin.Reports;

namespace CarRental.Integration.Tests.Reporting;

/// <summary>
/// The display rules of the reports screen and of its exports (no host needed): chart axes, formats of amounts, rates,
/// changes and waiting times, export addresses and file names, sheets of each export.
/// </summary>
public sealed class ReportDisplayTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    [Theory]
    [InlineData("85", "100", "25")]
    [InlineData("120", "150", "50")]
    [InlineData("3", "3", "1")]
    [InlineData("21.174", "30", "10")]
    [InlineData("12345", "15000", "5000")]
    [InlineData("0", "1", "0.25")]
    [InlineData("-5", "1", "0.25")]
    public void A_value_axis_starts_at_zero_and_ends_on_a_nice_tick_above_the_largest_value(string largest, string max, string step)
    {
        var scale = ChartScale.For(Parse(largest));

        scale.Max.ShouldBe(Parse(max));
        scale.Step.ShouldBe(Parse(step));
        scale.Ticks[0].ShouldBe(0m);
        scale.Ticks[^1].ShouldBe(scale.Max);
        scale.Ticks.Count.ShouldBeInRange(2, 6);
    }

    [Theory]
    [InlineData("0.179", "0.2", "0.05")]
    [InlineData("0.8", "0.8", "0.2")]
    [InlineData("0.95", "1", "0.25")]
    [InlineData("1", "1", "0.25")]
    [InlineData("0", "1", "0.25")]
    public void A_rate_axis_fits_low_rates_and_never_goes_beyond_one_hundred_percent(string largest, string max, string step)
    {
        var scale = ChartScale.ForRatio(Parse(largest));

        (scale.Max, scale.Step).ShouldBe((Parse(max), Parse(step)));
    }

    [Fact]
    public void Positions_on_the_axis_are_clamped_and_written_for_inline_styles()
    {
        var scale = ChartScale.For(85m);

        scale.Css(25m).ShouldBe("25%");
        scale.Css(12.5m).ShouldBe("12.5%");
        scale.Percent(150m).ShouldBe(100m);
        scale.Percent(-3m).ShouldBe(0m);
        scale.Fraction(50m).ShouldBe("0.5");
        ChartScale.For(0m, 1m).Ticks.ShouldBe([0m, 0.25m, 0.5m, 0.75m, 1m]);
    }

    [Fact]
    public void Amounts_rates_and_waiting_times_follow_the_user_culture()
    {
        InCulture(French, () =>
        {
            var amount = Money.Tnd(1234.5m);
            ReportFormat.Amount(amount).ShouldBe(amount.ToString("N", French));
            ReportFormat.Amount(amount).ShouldEndWith(" TND");
            ReportFormat.Number(amount).ShouldBe(1234.5m.ToString("N3", French));
            ReportFormat.Amount((Money?)null).ShouldBe("—");
            ReportFormat.Percent(0.4567m).ShouldBe(0.4567m.ToString("P1", French));
            ReportFormat.Percent(null).ShouldBe("—");
            ReportFormat.Month(new DateOnly(2026, 10, 21)).ShouldBe("Octobre 2026");
            ReportFormat.Duration(TimeSpan.FromMinutes(45)).ShouldBe("45 min");
            ReportFormat.Duration(TimeSpan.FromHours(2)).ShouldBe("2 h");
            ReportFormat.Duration(TimeSpan.FromMinutes(26 * 60 + 5)).ShouldBe("26 h 05 min");
            ReportFormat.Duration(null).ShouldBe("—");
            ReportFormat.Tick(1500m, 500m).ShouldBe(1.5m.ToString("0.#", French) + " k");
            ReportFormat.Tick(25m, 25m).ShouldBe("25");
            ReportFormat.PercentTick(0.05m, 0.05m).ShouldBe(0.05m.ToString("P0", French));
            ReportFormat.PercentTick(0.025m, 0.025m).ShouldBe(0.025m.ToString("P1", French));
        });
    }

    [Fact]
    public void Changes_carry_their_sign_and_their_direction_and_none_without_a_previous_value()
    {
        InCulture(French, () =>
        {
            var minus = French.NumberFormat.NegativeSign;
            ReportFormat.Change(110m, 100m).ShouldBe(new ReportDelta(1, "+" + 0.1m.ToString("P1", French)));
            ReportFormat.Change(90m, 100m).ShouldBe(new ReportDelta(-1, minus + 0.1m.ToString("P1", French)));
            ReportFormat.Change(Money.Tnd(-90m), Money.Tnd(-100m))!.Direction.ShouldBe(1, "a margin less negative is an improvement");
            ReportFormat.Change(100.04m, 100m).ShouldBe(new ReportDelta(0, 0m.ToString("P1", French)), "a change rounded away shows no direction");
            ReportFormat.Change(100m, 0m).ShouldBeNull();
            ReportFormat.Change((decimal?)null, 100m).ShouldBeNull();
            ReportFormat.Points(0.5m, 0.45m, "{0} pts").ShouldBe(new ReportDelta(1, "+" + 5m.ToString("N1", French) + " pts"));
            ReportFormat.Points(0.4m, null, "{0} pts").ShouldBeNull();
            ReportFormat.SignedPercent(-0.123m).ShouldBe(minus + 0.123m.ToString("P1", French));
            ReportFormat.SignedPercent(null).ShouldBe("—");
        });
    }

    [Fact]
    public void Exports_have_a_stable_address_per_table_and_an_ascii_file_name()
    {
        var agencyId = Guid.Parse("01a15e40-91de-75ae-9295-43ebd0bfc6c4");
        var from = new DateOnly(2026, 10, 1);
        var to = new DateOnly(2026, 10, 31);

        ReportExport.Url(ReportTable.Vehicles, agencyId, from, to)
            .ShouldBe("/admin/reports/export/vehicles?agency=01a15e40-91de-75ae-9295-43ebd0bfc6c4&from=2026-10-01&to=2026-10-31");
        ReportExport.FileName("rapport", "véhicules", "Agence Démo Tunis", from, to).ShouldBe("rapport-vehicules-agence-demo-tunis-2026-10-01-2026-10-31.xlsx");
        ReportExport.Slug("  Été 2026 / Sud  ").ShouldBe("ete-2026-sud");
        ReportExport.Slug("***").ShouldBe("export");
        ReportExport.Slug(new string('a', 60)).Length.ShouldBe(40);
        foreach (var table in Enum.GetValues<ReportTable>())
        {
            ReportExport.TryParse(ReportExport.Segment(table), out var parsed).ShouldBeTrue();
            parsed.ShouldBe(table);
        }

        ReportExport.TryParse("Vehicles", out _).ShouldBeFalse();
        ReportExport.TryParse("2", out _).ShouldBeFalse();
        ReportExport.TryParse(null, out _).ShouldBeFalse();
        ReportExport.TryParseDay("2026-10-01", out var day).ShouldBeTrue();
        day.ShouldBe(from);
        ReportExport.TryParseDay("01/10/2026", out _).ShouldBeFalse();
    }

    [Fact]
    public void An_export_holds_its_table_then_the_summary_and_the_full_export_every_table()
    {
        ReportWorkbook.SheetsOf(ReportTable.Pace).ShouldBe([ReportTable.Pace, ReportTable.All]);
        ReportWorkbook.SheetsOf(ReportTable.All).ShouldBe([ReportTable.All, .. Enum.GetValues<ReportTable>().Where(t => t != ReportTable.All)]);
    }

    private static decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static void InCulture(CultureInfo culture, Action action)
    {
        var (current, currentUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, culture);
            action();
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (current, currentUi);
        }
    }
}
