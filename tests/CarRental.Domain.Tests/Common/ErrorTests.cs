using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Common;

public sealed class ErrorTests
{
    [Theory]
    [InlineData("booking.not_found")]
    [InlineData("money.currency_mismatch")]
    [InlineData("a.b.c")]
    [InlineData("pricing.tier2.invalid")]
    public void Valid_codes_are_dotted_lower_case(string code)
    {
        new Error(code, "Message").Code.ShouldBe(code);
    }

    [Theory]
    [InlineData("NotFound")]
    [InlineData("booking")]
    [InlineData("Booking.not_found")]
    [InlineData("booking.")]
    [InlineData(".booking")]
    [InlineData("booking..x")]
    [InlineData("booking.not-found")]
    [InlineData("booking.1st")]
    public void Invalid_codes_are_rejected(string code)
    {
        Should.Throw<ArgumentException>(() => new Error(code, "Message"));
    }

    [Fact]
    public void Message_is_required()
    {
        Should.Throw<ArgumentException>(() => new Error("a.b", " "));
    }

    [Fact]
    public void Factories_set_the_kind()
    {
        Error.Validation("a.b", "m").Kind.ShouldBe(ErrorKind.Validation);
        Error.NotFound("a.b", "m").Kind.ShouldBe(ErrorKind.NotFound);
        Error.Conflict("a.b", "m").Kind.ShouldBe(ErrorKind.Conflict);
        Error.Forbidden("a.b", "m").Kind.ShouldBe(ErrorKind.Forbidden);
        Error.Failure("a.b", "m").Kind.ShouldBe(ErrorKind.Failure);
        new Error("a.b", "m").Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void Errors_have_value_equality()
    {
        Error.NotFound("a.b", "m").ShouldBe(Error.NotFound("a.b", "m"));
        Error.NotFound("a.b", "m").ShouldNotBe(Error.Conflict("a.b", "m"));
        Error.NotFound("a.b", "m").ToString().ShouldBe("a.b: m");
    }
}
