using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Common;

public sealed class DocumentNumberTests
{
    [Theory]
    [InlineData(DocumentType.Booking, 2027, 123, "LOC-2027-000123")]
    [InlineData(DocumentType.Invoice, 2027, 45, "FAC-2027-000045")]
    [InlineData(DocumentType.CreditNote, 2027, 3, "AV-2027-000003")]
    [InlineData(DocumentType.Invoice, 2028, 1_234_567, "FAC-2028-1234567")]
    public void Format_matches_the_specification(DocumentType type, int year, long sequence, string expected)
    {
        var number = DocumentNumber.Create(type, year, sequence).Value;

        number.Value.ShouldBe(expected);
        number.ToString().ShouldBe(expected);
        number.Type.ShouldBe(type);
        number.Year.ShouldBe(year);
        number.Sequence.ShouldBe(sequence);
    }

    [Fact]
    public void Create_validates_its_parts()
    {
        DocumentNumber.Create(DocumentType.Invoice, 1999, 1).Error.ShouldBe(DocumentNumberErrors.InvalidYear);
        DocumentNumber.Create(DocumentType.Invoice, 10000, 1).Error.ShouldBe(DocumentNumberErrors.InvalidYear);
        DocumentNumber.Create(DocumentType.Invoice, 2027, 0).Error.ShouldBe(DocumentNumberErrors.InvalidSequence);
        DocumentNumber.Create((DocumentType)99, 2027, 1).Error.ShouldBe(DocumentNumberErrors.UnknownType);
    }

    [Theory]
    [InlineData("LOC-2027-000123", DocumentType.Booking, 2027, 123)]
    [InlineData(" FAC-2027-000045 ", DocumentType.Invoice, 2027, 45)]
    [InlineData("AV-2030-1000000", DocumentType.CreditNote, 2030, 1_000_000)]
    public void Parse_round_trips(string value, DocumentType type, int year, long sequence)
    {
        var number = DocumentNumber.Parse(value).Value;

        number.ShouldBe(DocumentNumber.Create(type, year, sequence).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FAC-27-000045")]
    [InlineData("FAC-2027-45")]
    [InlineData("XYZ-2027-000045")]
    [InlineData("fac-2027-000045")]
    [InlineData("FAC-2027-000000")]
    public void Parse_rejects_malformed_numbers(string? value)
    {
        DocumentNumber.Parse(value).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Prefix_of_unknown_type_is_a_bug()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => DocumentNumber.PrefixOf((DocumentType)42));
    }
}
