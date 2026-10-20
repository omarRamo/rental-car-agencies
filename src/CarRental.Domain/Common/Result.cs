using System.Diagnostics.CodeAnalysis;

namespace CarRental.Domain.Common;

/// <summary>
/// Outcome of an operation that can fail for a business reason. Exceptions are reserved for bugs and outages;
/// expected failures (rule violated, not found, conflict) are returned as a failed result carrying an <see cref="Common.Error"/>.
/// </summary>
public class Result
{
    private static readonly Result SuccessResult = new(null);
    private readonly Error? _error;

    protected Result(Error? error) => _error = error;

    public bool IsSuccess => _error is null;

    public bool IsFailure => _error is not null;

    /// <summary>The error of a failed result. Accessing it on a successful result is a bug and throws.</summary>
    public Error Error => _error ?? throw new InvalidOperationException("A successful result has no error.");

    public static Result Success() => SuccessResult;

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    public static Result<TValue> Success<TValue>(TValue value) => new(value);

    public static Result<TValue> Failure<TValue>(Error error) => new(error);

    /// <summary>Returns the first failure among <paramref name="results"/>, or success when all succeeded.</summary>
    public static Result Combine(params ReadOnlySpan<Result> results)
    {
        foreach (var result in results)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (result.IsFailure)
            {
                return result;
            }
        }

        return SuccessResult;
    }

    public static implicit operator Result(Error error) => Failure(error);

    public override string ToString() => IsSuccess ? "Success" : $"Failure({_error})";
}

/// <summary>Outcome of an operation that returns a <typeparamref name="TValue"/> on success.</summary>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue value)
        : base(null) => _value = value;

    internal Result(Error error)
        : base(error ?? throw new ArgumentNullException(nameof(error))) => _value = default;

    /// <summary>The value of a successful result. Accessing it on a failed result is a bug and throws.</summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot access the value of a failed result ({Error.Code}).");

    public static implicit operator Result<TValue>(TValue value) => new(value);

    public static implicit operator Result<TValue>(Error error) => new(error);

    public bool TryGetValue([MaybeNullWhen(false)] out TValue value)
    {
        value = IsSuccess ? _value! : default;
        return IsSuccess;
    }

    public Result<TOut> Map<TOut>(Func<TValue, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? new Result<TOut>(map(_value!)) : new Result<TOut>(Error);
    }

    public Result<TOut> Bind<TOut>(Func<TValue, Result<TOut>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? bind(_value!) : new Result<TOut>(Error);
    }

    public TOut Match<TOut>(Func<TValue, TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(_value!) : onFailure(Error);
    }

    public override string ToString() => IsSuccess ? $"Success({_value})" : base.ToString();
}
