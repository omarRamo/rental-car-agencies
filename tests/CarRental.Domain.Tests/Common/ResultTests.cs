using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Common;

public sealed class ResultTests
{
    private static readonly Error SampleError = Error.Validation("sample.invalid", "Sample error.");

    [Fact]
    public void Success_has_no_error()
    {
        var result = Result.Success();

        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => result.Error);
        result.ToString().ShouldBe("Success");
    }

    [Fact]
    public void Failure_carries_its_error()
    {
        var result = Result.Failure(SampleError);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(SampleError);
        result.ToString().ShouldContain("sample.invalid");
        Should.Throw<ArgumentNullException>(() => Result.Failure(null!));
    }

    [Fact]
    public void Errors_convert_implicitly_to_results()
    {
        Result untyped = SampleError;
        Result<int> typed = SampleError;

        untyped.Error.ShouldBe(SampleError);
        typed.Error.ShouldBe(SampleError);
    }

    [Fact]
    public void Typed_success_exposes_its_value()
    {
        Result<int> result = 42;

        result.Value.ShouldBe(42);
        result.TryGetValue(out var value).ShouldBeTrue();
        value.ShouldBe(42);
        result.ToString().ShouldBe("Success(42)");
        Result.Success("ok").Value.ShouldBe("ok");
    }

    [Fact]
    public void Accessing_the_value_of_a_failure_is_a_bug()
    {
        var result = Result.Failure<int>(SampleError);

        Should.Throw<InvalidOperationException>(() => result.Value).Message.ShouldContain("sample.invalid");
        result.TryGetValue(out var value).ShouldBeFalse();
        value.ShouldBe(0);
    }

    [Fact]
    public void Map_and_bind_propagate_failures()
    {
        Result<int> success = 21;
        Result<int> failure = SampleError;

        success.Map(x => x * 2).Value.ShouldBe(42);
        failure.Map(x => x * 2).Error.ShouldBe(SampleError);
        success.Bind(x => Result.Success(x.ToString(System.Globalization.CultureInfo.InvariantCulture))).Value.ShouldBe("21");
        success.Bind<string>(_ => SampleError).Error.ShouldBe(SampleError);
        failure.Bind(x => Result.Success(x + 1)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Match_selects_the_branch()
    {
        Result<int> success = 1;
        Result<int> failure = SampleError;

        success.Match(v => $"value {v}", e => e.Code).ShouldBe("value 1");
        failure.Match(v => $"value {v}", e => e.Code).ShouldBe("sample.invalid");
    }

    [Fact]
    public void Combine_returns_the_first_failure()
    {
        var other = Error.Conflict("sample.conflict", "Other.");

        Result.Combine(Result.Success(), Result.Success()).IsSuccess.ShouldBeTrue();
        Result.Combine(Result.Success(), SampleError, other).Error.ShouldBe(SampleError);
        Result.Combine().IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Functional_helpers_reject_null_delegates()
    {
        Result<int> result = 1;

        Should.Throw<ArgumentNullException>(() => result.Map<int>(null!));
        Should.Throw<ArgumentNullException>(() => result.Bind<int>(null!));
        Should.Throw<ArgumentNullException>(() => result.Match<int>(null!, _ => 0));
        Should.Throw<ArgumentNullException>(() => result.Match(_ => 0, null!));
    }
}
