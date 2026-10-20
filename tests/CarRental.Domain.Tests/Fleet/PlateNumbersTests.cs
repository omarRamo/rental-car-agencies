using CarRental.Domain.Fleet;

namespace CarRental.Domain.Tests.Fleet;

public sealed class PlateNumbersTests
{
    [Theory]
    [InlineData("215 TU 4512", "215 TU 4512")]
    [InlineData("  215   tu\t4512 ", "215 TU 4512")]
    [InlineData("rs-123456", "RS-123456")]
    [InlineData("215 تونس 4512", "215 تونس 4512")]
    [InlineData("AB", "AB")]
    public void Plates_are_trimmed_upper_cased_and_single_spaced(string input, string expected)
    {
        PlateNumbers.Normalize(input).Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A")]
    [InlineData("215/TU/4512")]
    [InlineData("215 TU 4512 ABCDEFGHIJ")]
    [InlineData("215_TU")]
    public void Invalid_plates_are_rejected(string? input)
    {
        PlateNumbers.Normalize(input).Error.ShouldBe(FleetErrors.PlateNumberInvalid);
    }
}
