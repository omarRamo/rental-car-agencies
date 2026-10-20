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
}
