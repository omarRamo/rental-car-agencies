using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Common;

public sealed class AcrissCodeTests
{
    [Theory]
    [InlineData("MCMR")]
    [InlineData("ECMR")]
    [InlineData("EDAR")]
    [InlineData("CDMR")]
    [InlineData("IFMR")]
    [InlineData("XKND")]
    [InlineData("WTDE")]
    public void Demo_and_valid_codes_are_accepted(string code)
    {
        var result = AcrissCode.Create(code);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(code);
        result.Value.ToString().ShouldBe(code);
    }

    [Fact]
    public void Code_is_normalized_to_upper_case_without_spaces()
    {
        AcrissCode.Create("  ecmr ").Value.Value.ShouldBe("ECMR");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Code_is_required(string? code)
    {
        AcrissCode.Create(code).Error.ShouldBe(AcrissCodeErrors.Required);
    }

    [Theory]
    [InlineData("ECM")]
    [InlineData("ECMRX")]
    public void Code_has_four_letters(string code)
    {
        AcrissCode.Create(code).Error.ShouldBe(AcrissCodeErrors.InvalidLength);
    }

    [Theory]
    [InlineData("ACMR", "acriss.invalid_category")]
    [InlineData("EAMR", "acriss.invalid_type")]
    [InlineData("ECXR", "acriss.invalid_transmission")]
    [InlineData("ECMY", "acriss.invalid_fuel_air")]
    [InlineData("1CMR", "acriss.invalid_category")]
    public void Each_position_is_validated(string code, string expectedError)
    {
        AcrissCode.Create(code).Error.Code.ShouldBe(expectedError);
    }

    [Theory]
    [InlineData("EDAR", true)]
    [InlineData("CDBR", true)]
    [InlineData("IFDR", true)]
    [InlineData("ECMR", false)]
    [InlineData("IFNR", false)]
    [InlineData("IFCR", false)]
    public void IsAutomatic_reads_the_third_letter(string code, bool expected)
    {
        AcrissCode.Create(code).Value.IsAutomatic.ShouldBe(expected);
    }

    [Theory]
    [InlineData("ECMR", true)]
    [InlineData("ECMN", false)]
    [InlineData("ECMD", true)]
    [InlineData("ECMQ", false)]
    [InlineData("ECMV", true)]
    [InlineData("ECMZ", false)]
    [InlineData("ECME", true)]
    public void HasAirConditioning_reads_the_fourth_letter(string code, bool expected)
    {
        AcrissCode.Create(code).Value.HasAirConditioning.ShouldBe(expected);
    }

    [Fact]
    public void Positions_are_exposed()
    {
        var code = AcrissCode.Create("IFMR").Value;

        code.Category.ShouldBe('I');
        code.Type.ShouldBe('F');
        code.TransmissionAndDrive.ShouldBe('M');
        code.FuelAndAir.ShouldBe('R');
    }

    [Fact]
    public void Equality_is_by_value()
    {
        (AcrissCode.Create("ecmr").Value == AcrissCode.Create("ECMR").Value).ShouldBeTrue();
        (AcrissCode.Create("ECMR").Value != AcrissCode.Create("EDAR").Value).ShouldBeTrue();
        AcrissCode.Create("ECMR").Value.GetHashCode().ShouldBe(AcrissCode.Create("ecmr").Value.GetHashCode());
        AcrissCode.Create("ECMR").Value.Equals((object)AcrissCode.Create("ECMR").Value).ShouldBeTrue();
        AcrissCode.Create("ECMR").Value.Equals(null).ShouldBeFalse();
    }
}
