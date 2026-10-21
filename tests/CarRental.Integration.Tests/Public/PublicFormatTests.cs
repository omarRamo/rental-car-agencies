using System.Globalization;
using CarRental.Domain.Common;
using CarRental.Web.Components.Public.Site;

namespace CarRental.Integration.Tests.Public;

/// <summary>Display of amounts: TND with millimes, indicative euros rounded to the euro.</summary>
public sealed class PublicFormatTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    [Theory]
    [InlineData(681.400, 3.40, 200)]
    [InlineData(1.700, 3.40, 1)]
    [InlineData(1.699, 3.40, 0)]
    [InlineData(5.100, 3.40, 2)]
    [InlineData(340.000, 3.40, 100)]
    [InlineData(1234.567, 3.25, 380)]
    [InlineData(0, 3.40, 0)]
    public void The_euro_equivalent_is_rounded_to_the_euro_away_from_zero(decimal tnd, decimal rate, decimal expected)
    {
        PublicFormat.EuroEquivalent(Money.Tnd(tnd), rate).ShouldBe(expected);
    }

    [Fact]
    public void There_is_no_euro_equivalent_without_a_positive_rate_or_for_another_currency()
    {
        PublicFormat.EuroEquivalent(Money.Tnd(100m), 0m).ShouldBeNull();
        PublicFormat.EuroEquivalent(Money.Tnd(100m), -3.4m).ShouldBeNull();
        PublicFormat.EuroEquivalent(Money.Of(100m, Currency.Eur), 3.4m).ShouldBeNull();
    }

    [Fact]
    public void Euros_follow_the_culture_and_dinars_keep_their_millimes()
    {
        Normalize(PublicFormat.Euros(200m, French)).ShouldBe("200 €");
        PublicFormat.Euros(1200m, English).ShouldBe("€1,200");
        PublicFormat.Price(Money.Tnd(681.4m), English).ShouldBe("681.400 TND");
        Normalize(PublicFormat.Price(Money.Tnd(1234.5m), French)).ShouldBe("1 234,500 TND");
    }

    /// <summary>Spaces of the culture (no-break, narrow no-break) as plain spaces.</summary>
    private static string Normalize(string text) => text.Replace('\u202f', ' ').Replace('\u00a0', ' ');

    [Theory]
    [InlineData("+216 70 000 000", "tel:+21670000000")]
    [InlineData("70 000 000", "tel:70000000")]
    [InlineData("(+216) 98-123-456", "tel:+21698123456")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    [InlineData("n/a", null)]
    public void Phone_numbers_become_tel_links(string? phone, string? expected)
    {
        PublicFormat.TelephoneLink(phone).ShouldBe(expected);
    }

    [Fact]
    public void Times_use_the_24_hour_clock()
    {
        PublicFormat.Time(new TimeOnly(8, 0)).ShouldBe("08:00");
        PublicFormat.Time(new TimeOnly(20, 30)).ShouldBe("20:30");
        PublicFormat.Date(new DateTimeOffset(2026, 10, 24, 23, 30, 0, TimeSpan.Zero), English).ShouldBe("25 October 2026");
    }
}
