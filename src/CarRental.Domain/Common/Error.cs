using System.Text.RegularExpressions;

namespace CarRental.Domain.Common;

/// <summary>
/// A business error: a stable, machine-readable <see cref="Code"/> made of lower-case dotted segments
/// (e.g. <c>period.end_must_be_after_start</c>) and a developer-oriented English <see cref="Message"/>.
/// </summary>
/// <remarks>
/// Codes are part of the public contract (logs, API responses, UI resource keys): never rename a published code.
/// User-facing texts are looked up from resources by code; <see cref="Message"/> is not shown to end users.
/// </remarks>
public sealed partial record Error
{
    public Error(string code, string message, ErrorKind kind = ErrorKind.Validation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!CodePattern().IsMatch(code))
        {
            throw new ArgumentException(
                $"Error code '{code}' must be made of lower-case dotted segments, for example 'booking.not_found'.",
                nameof(code));
        }

        Code = code;
        Message = message;
        Kind = kind;
    }

    /// <summary>Stable dotted error code, for example <c>money.currency_mismatch</c>.</summary>
    public string Code { get; }

    /// <summary>Developer-oriented description (English). Not localized, not displayed to end users.</summary>
    public string Message { get; }

    /// <summary>Category of the error.</summary>
    public ErrorKind Kind { get; }

    public static Error Validation(string code, string message) => new(code, message, ErrorKind.Validation);

    public static Error NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorKind.Forbidden);

    public static Error Failure(string code, string message) => new(code, message, ErrorKind.Failure);

    public override string ToString() => $"{Code}: {Message}";

    [GeneratedRegex("^[a-z][a-z0-9_]*([.][a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
