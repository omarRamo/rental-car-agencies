using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Domain.Accounting;

/// <summary>
/// A calendar month of Tunis (the accounting period of the monthly closing, SPEC §8): from its first to its last day,
/// both included. Written <c>yyyy-MM</c>.
/// </summary>
public readonly record struct AccountingMonth : IComparable<AccountingMonth>
{
    private AccountingMonth(int year, int month)
    {
        Year = year;
        Month = month;
    }

    public int Year { get; }

    public int Month { get; }

    public DateOnly FirstDay => new(Year, Month, 1);

    public DateOnly LastDay => FirstDay.AddMonths(1).AddDays(-1);

    public static bool operator <(AccountingMonth left, AccountingMonth right) => left.CompareTo(right) < 0;

    public static bool operator <=(AccountingMonth left, AccountingMonth right) => left.CompareTo(right) <= 0;

    public static bool operator >(AccountingMonth left, AccountingMonth right) => left.CompareTo(right) > 0;

    public static bool operator >=(AccountingMonth left, AccountingMonth right) => left.CompareTo(right) >= 0;

    public static Result<AccountingMonth> Create(int year, int month) =>
        year is < 2000 or > 2100 || month is < 1 or > 12 ? AccountingErrors.MonthInvalid : new AccountingMonth(year, month);

    /// <summary>The month of a Tunis calendar day.</summary>
    public static AccountingMonth Of(DateOnly day) => new(day.Year, day.Month);

    /// <summary>Parses <c>yyyy-MM</c>.</summary>
    public static Result<AccountingMonth> Parse(string? value) =>
        value is not null
        && DateOnly.TryParseExact(value.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)
            ? Create(first.Year, first.Month)
            : AccountingErrors.MonthInvalid;

    public bool Contains(DateOnly day) => day.Year == Year && day.Month == Month;

    /// <summary>True once the month is over on <paramref name="today"/> (its last day is in the past).</summary>
    public bool IsOverOn(DateOnly today) => today > LastDay;

    public AccountingMonth Next() => Of(FirstDay.AddMonths(1));

    public AccountingMonth Previous() => Of(FirstDay.AddMonths(-1));

    public int CompareTo(AccountingMonth other) => (Year * 12 + Month).CompareTo(other.Year * 12 + other.Month);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-{Month:D2}");
}
