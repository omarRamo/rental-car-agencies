using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Common;

public sealed class CurrencyTests
{
    [Theory]
    [InlineData("TND", 3)]
    [InlineData("tnd", 3)]
    [InlineData(" EUR ", 2)]
    [InlineData("USD", 2)]
    public void FromCode_finds_known_currencies(string code, int minorUnits)
    {
        var result = Currency.FromCode(code);

        result.IsSuccess.ShouldBeTrue();
        result.Value.MinorUnits.ShouldBe(minorUnits);
        result.Value.Code.ShouldBe(code.Trim().ToUpperInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void FromCode_requires_a_code(string? code)
    {
        Currency.FromCode(code).Error.ShouldBe(MoneyErrors.CurrencyRequired);
    }

    [Fact]
    public void FromCode_rejects_unknown_currencies()
    {
        Currency.FromCode("XYZ").Error.ShouldBe(MoneyErrors.UnknownCurrency);
    }

    [Fact]
    public void Equality_is_by_code()
    {
        (Currency.FromCode("tnd").Value == Currency.Tnd).ShouldBeTrue();
        (Currency.Tnd != Currency.Eur).ShouldBeTrue();
        Currency.Tnd.Equals((object)Currency.Tnd).ShouldBeTrue();
        Currency.Tnd.Equals(null).ShouldBeFalse();
        Currency.Tnd.GetHashCode().ShouldBe(Currency.FromCode("TND").Value.GetHashCode());
        Currency.Tnd.ToString().ShouldBe("TND");
        Currency.All.ShouldContain(Currency.Tnd);
    }
}
