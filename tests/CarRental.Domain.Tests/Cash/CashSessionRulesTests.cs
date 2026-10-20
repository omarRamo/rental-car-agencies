using CarRental.Domain.Cash;
using static CarRental.Domain.Tests.Cash.CashTestData;

namespace CarRental.Domain.Tests.Cash;

/// <summary>One session per cashier, agency and Tunis day, and at most one open session per cashier.</summary>
public sealed class CashSessionRulesTests
{
    private static readonly DateOnly Today = new(2027, 6, 28);

    [Fact]
    public void A_cashier_without_session_may_open_one()
    {
        CashSessionRules.EnsureCanOpen([], AgencyId, Today).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-30)]
    public void An_open_session_must_be_closed_before_another_one_opens(int daysAgo)
    {
        ExistingCashSession[] sessions = [new(Guid.CreateVersion7(), Today.AddDays(daysAgo), CashSessionStatus.Open)];

        CashSessionRules.EnsureCanOpen(sessions, AgencyId, Today).Error.ShouldBe(CashErrors.SessionAlreadyOpen);
    }

    [Theory]
    [InlineData(CashSessionStatus.Closed)]
    [InlineData(CashSessionStatus.Locked)]
    public void A_closed_session_is_not_reopened_the_same_day_in_the_same_agency(CashSessionStatus status)
    {
        ExistingCashSession[] sessions = [new(AgencyId, Today, status)];

        CashSessionRules.EnsureCanOpen(sessions, AgencyId, Today).Error.ShouldBe(CashErrors.SessionAlreadyExistsForDay);
    }

    [Fact]
    public void A_session_of_another_day_or_agency_does_not_prevent_opening()
    {
        ExistingCashSession[] sessions =
        [
            new(AgencyId, Today.AddDays(-1), CashSessionStatus.Locked),
            new(Guid.CreateVersion7(), Today, CashSessionStatus.Closed),
        ];

        CashSessionRules.EnsureCanOpen(sessions, AgencyId, Today).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("2027-06-27T22:59:59Z", "2027-06-27")]
    [InlineData("2027-06-27T23:00:00Z", "2027-06-28")]
    [InlineData("2027-01-15T23:30:00Z", "2027-01-16")]
    public void The_business_date_is_the_tunis_day_of_the_opening(string openedAt, string expected)
    {
        CashSessionRules.BusinessDateOf(DateTimeOffset.Parse(openedAt, System.Globalization.CultureInfo.InvariantCulture))
            .ShouldBe(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }
}
