using CarRental.Domain.Common;

namespace CarRental.Domain.Payments;

/// <summary>Trimming and validation of the free texts and amounts of the Payments module.</summary>
internal static class PaymentText
{
    /// <summary>Trimmed text, or <see langword="null"/> for a blank value.</summary>
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The name of an actor, cut to the stored length.</summary>
    public static string? ActorName(AuditActor actor)
    {
        var name = Clean(actor.UserName);
        return name is { Length: > AuditActor.UserNameMaxLength } ? name[..AuditActor.UserNameMaxLength] : name;
    }

    /// <summary>A positive amount in TND up to <paramref name="maximum"/> (the struct is already rounded to the millime).</summary>
    public static bool IsValidAmount(Money amount, decimal maximum) =>
        amount.Currency == Currency.Tnd && amount.IsPositive && amount.Amount <= maximum;

    /// <summary>
    /// A local path of the site (where the browser is sent back after the payment page): rooted, not protocol-relative
    /// (<c>//host</c>, <c>/\host</c>), without backslash, white space or control character — never an open redirect.
    /// </summary>
    public static bool IsLocalPath(string? path, int maximumLength) =>
        path is { Length: > 0 } value
        && value.Length <= maximumLength
        && value[0] == '/'
        && (value.Length == 1 || (value[1] != '/' && value[1] != '\\'))
        && !value.Any(c => c == '\\' || char.IsWhiteSpace(c) || char.IsControl(c));

    /// <summary>A text cut to <paramref name="maximumLength"/> characters (diagnostic details).</summary>
    public static string? Truncate(string? value, int maximumLength)
    {
        var cleaned = Clean(value);
        return cleaned is { Length: > 0 } text && text.Length > maximumLength ? text[..maximumLength] : cleaned;
    }
}
