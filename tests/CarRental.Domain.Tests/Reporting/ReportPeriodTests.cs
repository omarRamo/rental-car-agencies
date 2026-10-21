using CarRental.Domain.Reporting;

namespace CarRental.Domain.Tests.Reporting;

/// <summary>Reporting periods: months by default, compared with the same elapsed days of the previous period (ADR-0046).</summary>
public sealed class ReportPeriodTests
{
    [Fact]
    public void A_month_runs_from_its_first_to_its_last_day()
    {
        var february = ReportPeriod.Month(2028, 2);

        february.From.ShouldBe(new DateOnly(2028, 2, 1));
        february.To.ShouldBe(new DateOnly(2028, 2, 29));
        february.Days.ShouldBe(29);
        february.IsCalendarMonth.ShouldBeTrue();
        ReportPeriod.MonthOf(new DateOnly(2026, 10, 21)).ShouldBe(ReportPeriod.Month(2026, 10));
    }

    [Theory]
    [InlineData("2026-10-21", "2026-10-01", "2026-10-31")]
    [InlineData("2026-10-02", "2026-10-01", "2026-10-31")]
    [InlineData("2026-10-01", "2026-09-01", "2026-09-30")]
    [InlineData("2027-01-01", "2026-12-01", "2026-12-31")]
    [InlineData("2028-03-01", "2028-02-01", "2028-02-29")]
    public void The_reports_open_on_the_current_month_or_on_the_previous_one_on_its_first_day(string today, string from, string to)
    {
        var period = ReportPeriod.DefaultFor(DateOnly.Parse(today, System.Globalization.CultureInfo.InvariantCulture));

        period.From.ShouldBe(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture));
        period.To.ShouldBe(DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture));
        period.IsCalendarMonth.ShouldBeTrue();
    }

    [Fact]
    public void The_default_period_always_has_an_elapsed_day()
    {
        for (var today = new DateOnly(2026, 1, 1); today <= new DateOnly(2028, 12, 31); today = today.AddDays(1))
        {
            ReportPeriod.DefaultFor(today).Until(today.AddDays(-1)).ShouldNotBeNull($"{today:yyyy-MM-dd}");
        }
    }

    [Fact]
    public void A_period_ends_after_it_starts_and_lasts_a_year_at_most()
    {
        ReportPeriod.Create(new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 1)).Error.ShouldBe(ReportingErrors.PeriodInvalid);
        ReportPeriod.Create(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 2)).Error.ShouldBe(ReportingErrors.PeriodTooLong);
        ReportPeriod.Create(new DateOnly(2028, 1, 1), new DateOnly(2028, 12, 31)).Value.Days.ShouldBe(366);
        ReportPeriod.Create(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 5)).Value.Days.ShouldBe(1);
    }

    [Fact]
    public void A_custom_range_is_not_a_calendar_month()
    {
        ReportPeriod.Create(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 30)).Value.IsCalendarMonth.ShouldBeFalse();
        ReportPeriod.Create(new DateOnly(2026, 10, 2), new DateOnly(2026, 11, 1)).Value.IsCalendarMonth.ShouldBeFalse();
    }

    [Fact]
    public void Until_cuts_the_days_not_elapsed_yet()
    {
        var october = ReportPeriod.Month(2026, 10);

        october.Until(new DateOnly(2026, 10, 20))!.To.ShouldBe(new DateOnly(2026, 10, 20));
        october.Until(new DateOnly(2026, 11, 3)).ShouldBe(october);
        october.Until(new DateOnly(2026, 9, 30)).ShouldBeNull();
    }

    [Theory]
    [InlineData(2026, 10, 20, "2026-09-01", "2026-09-20")]
    [InlineData(2026, 10, 31, "2026-09-01", "2026-09-30")]
    [InlineData(2027, 3, 31, "2027-02-01", "2027-02-28")]
    [InlineData(2027, 1, 15, "2026-12-01", "2026-12-15")]
    public void A_month_compares_with_the_same_days_of_the_previous_month(int year, int month, int elapsedDays, string from, string to)
    {
        var previous = ReportPeriod.Month(year, month).Previous(elapsedDays);

        previous.From.ShouldBe(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture));
        previous.To.ShouldBe(DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_custom_range_compares_with_as_many_days_just_before()
    {
        var range = ReportPeriod.Create(new DateOnly(2026, 10, 11), new DateOnly(2026, 10, 20)).Value;

        var previous = range.Previous(range.Days);

        previous.From.ShouldBe(new DateOnly(2026, 10, 1));
        previous.To.ShouldBe(new DateOnly(2026, 10, 10));
        Should.Throw<ArgumentOutOfRangeException>(() => range.Previous(11));
        Should.Throw<ArgumentOutOfRangeException>(() => range.Previous(0));
    }

    [Fact]
    public void Each_day_lists_the_days_in_order()
    {
        var range = ReportPeriod.Create(new DateOnly(2026, 12, 30), new DateOnly(2027, 1, 2)).Value;

        range.EachDay().ShouldBe([new(2026, 12, 30), new(2026, 12, 31), new(2027, 1, 1), new(2027, 1, 2)]);
        range.Contains(new DateOnly(2027, 1, 1)).ShouldBeTrue();
        range.Contains(new DateOnly(2027, 1, 3)).ShouldBeFalse();
    }
}
