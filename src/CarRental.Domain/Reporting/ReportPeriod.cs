using CarRental.Domain.Common;

namespace CarRental.Domain.Reporting;

/// <summary>
/// A reporting period: Tunis calendar days from <see cref="From"/> to <see cref="To"/>, both included, at most
/// <see cref="MaxDays"/> days. A calendar month is the default period of the reports (ADR-0046).
/// </summary>
public sealed record ReportPeriod
{
    /// <summary>A leap year: the longest period a report or a recomputation covers.</summary>
    public const int MaxDays = 366;

    private ReportPeriod(DateOnly from, DateOnly to)
    {
        From = from;
        To = to;
    }

    public DateOnly From { get; }

    public DateOnly To { get; }

    public int Days => To.DayNumber - From.DayNumber + 1;

    /// <summary>The period is exactly one calendar month.</summary>
    public bool IsCalendarMonth => From.Day == 1 && To == From.AddMonths(1).AddDays(-1);

    public static Result<ReportPeriod> Create(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            return ReportingErrors.PeriodInvalid;
        }

        return to.DayNumber - from.DayNumber + 1 > MaxDays ? ReportingErrors.PeriodTooLong : new ReportPeriod(from, to);
    }

    /// <summary>The calendar month <paramref name="year"/>-<paramref name="month"/>.</summary>
    public static ReportPeriod Month(int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        return new ReportPeriod(first, first.AddMonths(1).AddDays(-1));
    }

    /// <summary>The calendar month of <paramref name="day"/>.</summary>
    public static ReportPeriod MonthOf(DateOnly day) => Month(day.Year, day.Month);

    /// <summary>
    /// The period the reports open on (ADR-0046): the current month, or the previous one on the first day of a month,
    /// when the current month has no elapsed day yet.
    /// </summary>
    public static ReportPeriod DefaultFor(DateOnly today) => MonthOf(today.Day == 1 ? today.AddDays(-1) : today);

    public bool Contains(DateOnly day) => day >= From && day <= To;

    /// <summary>The part of the period up to <paramref name="lastDay"/> included, or <see langword="null"/> when it starts later.</summary>
    public ReportPeriod? Until(DateOnly lastDay)
    {
        if (lastDay < From)
        {
            return null;
        }

        return lastDay >= To ? this : new ReportPeriod(From, lastDay);
    }

    /// <summary>
    /// The period to compare with, of <paramref name="days"/> days (the elapsed part of this period): for a calendar
    /// month, the previous month from its first day (cut at its end), so that a month in progress compares with the same
    /// days of the previous month; otherwise the <paramref name="days"/> days just before <see cref="From"/>.
    /// </summary>
    public ReportPeriod Previous(int days)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(days, Days);
        if (IsCalendarMonth)
        {
            var month = MonthOf(From.AddMonths(-1));
            var end = month.From.AddDays(days - 1);
            return new ReportPeriod(month.From, end < month.To ? end : month.To);
        }

        return new ReportPeriod(From.AddDays(-days), From.AddDays(-1));
    }

    /// <summary>Every day of the period, in order.</summary>
    public IEnumerable<DateOnly> EachDay()
    {
        for (var day = From; day <= To; day = day.AddDays(1))
        {
            yield return day;
        }
    }
}
